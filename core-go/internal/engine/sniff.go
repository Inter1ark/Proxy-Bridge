package engine

import (
	"bytes"
	"encoding/binary"
	"net"
	"strings"
	"time"
)

// Sniffing reads the first bytes a program sends on a new connection and finds the name of
// the site in them: the server name of a TLS ClientHello (HTTPS) or the Host header of a
// plain HTTP request. The proxy is then asked to connect to that name instead of the IP
// address, so the name is resolved on the proxy's side. Only client-first protocols on web
// ports are sniffed; everything else is dialled by address right away.

// sniffPorts are the destination ports where the program speaks first.
var sniffPorts = map[uint16]bool{80: true, 443: true, 8080: true, 8443: true}

const (
	sniffWait = 400 * time.Millisecond // how long to wait for the first bytes
	sniffMax  = 16 * 1024              // a ClientHello with post-quantum key shares is about 2 KB
)

// sniffHost reads from client until a host name is found, the data cannot contain one, the
// buffer is full or the wait is over. It returns the bytes read (to be sent upstream first)
// and the host name, "" when there is none.
func sniffHost(client net.Conn) ([]byte, string) {
	buf := make([]byte, 0, 4096)
	tmp := make([]byte, 4096)
	deadline := time.Now().Add(sniffWait)
	_ = client.SetReadDeadline(deadline)
	defer client.SetReadDeadline(time.Time{})
	for len(buf) < sniffMax {
		n, err := client.Read(tmp)
		if n > 0 {
			buf = append(buf, tmp[:n]...)
			host, more := parseHost(buf)
			if host != "" || !more {
				return buf, host
			}
		}
		if err != nil {
			break
		}
	}
	return buf, ""
}

// parseHost finds a host name in the first bytes of a connection. more is true when the data
// looks like TLS or HTTP but is not complete yet.
func parseHost(b []byte) (host string, more bool) {
	if len(b) == 0 {
		return "", true
	}
	if b[0] == 0x16 {
		return parseSNI(b)
	}
	if looksLikeHTTP(b) {
		return parseHTTPHost(b)
	}
	return "", false
}

// parseSNI reads the server_name extension of a TLS ClientHello (it may span several records'
// worth of TCP segments, but a ClientHello is a single handshake message in the first record).
func parseSNI(b []byte) (string, bool) {
	if len(b) < 5 {
		return "", true
	}
	if b[0] != 0x16 || b[1] != 0x03 {
		return "", false
	}
	recLen := int(binary.BigEndian.Uint16(b[3:5]))
	if len(b) < 5+recLen {
		return "", true
	}
	h := b[5 : 5+recLen]
	if len(h) < 4 || h[0] != 0x01 { // ClientHello
		return "", false
	}
	hsLen := int(h[1])<<16 | int(h[2])<<8 | int(h[3])
	if hsLen > len(h)-4 {
		// the ClientHello continues in the next record: sniff what we have
		hsLen = len(h) - 4
	}
	p := h[4 : 4+hsLen]
	// version(2) random(32)
	if len(p) < 34 {
		return "", false
	}
	p = p[34:]
	// session id
	if len(p) < 1 || len(p) < 1+int(p[0]) {
		return "", false
	}
	p = p[1+int(p[0]):]
	// cipher suites
	if len(p) < 2 {
		return "", false
	}
	n := int(binary.BigEndian.Uint16(p))
	if len(p) < 2+n {
		return "", false
	}
	p = p[2+n:]
	// compression methods
	if len(p) < 1 || len(p) < 1+int(p[0]) {
		return "", false
	}
	p = p[1+int(p[0]):]
	// extensions
	if len(p) < 2 {
		return "", false
	}
	n = int(binary.BigEndian.Uint16(p))
	p = p[2:]
	if n < len(p) {
		p = p[:n]
	}
	for len(p) >= 4 {
		typ := binary.BigEndian.Uint16(p)
		l := int(binary.BigEndian.Uint16(p[2:]))
		if len(p) < 4+l {
			return "", false
		}
		if typ == 0 { // server_name
			e := p[4 : 4+l]
			if len(e) < 2 {
				return "", false
			}
			e = e[2:]
			for len(e) >= 3 {
				nt := e[0]
				nl := int(binary.BigEndian.Uint16(e[1:]))
				if len(e) < 3+nl {
					return "", false
				}
				if nt == 0 {
					return cleanHost(string(e[3 : 3+nl])), false
				}
				e = e[3+nl:]
			}
			return "", false
		}
		p = p[4+l:]
	}
	return "", false
}

var httpMethods = []string{"GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "CONNECT ", "TRACE "}

func looksLikeHTTP(b []byte) bool {
	for _, m := range httpMethods {
		k := len(m)
		if len(b) >= k && string(b[:k]) == m {
			return true
		}
		if len(b) < k && strings.HasPrefix(m, string(b)) {
			return true // not enough bytes yet to tell
		}
	}
	return false
}

func parseHTTPHost(b []byte) (string, bool) {
	end := bytes.Index(b, []byte("\r\n\r\n"))
	head := b
	if end >= 0 {
		head = b[:end]
	}
	for _, line := range bytes.Split(head, []byte("\r\n"))[1:] {
		if len(line) > 5 && strings.EqualFold(string(line[:5]), "host:") {
			h := strings.TrimSpace(string(line[5:]))
			if hh, _, err := net.SplitHostPort(h); err == nil {
				h = hh
			}
			return cleanHost(h), false
		}
	}
	return "", end < 0
}

// cleanHost accepts a DNS name only: no IP literals, no odd characters.
func cleanHost(h string) string {
	h = strings.TrimSuffix(strings.ToLower(strings.TrimSpace(h)), ".")
	if len(h) == 0 || len(h) > 253 || !strings.Contains(h, ".") {
		return ""
	}
	if net.ParseIP(strings.Trim(h, "[]")) != nil {
		return ""
	}
	for _, c := range h {
		if !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.' || c == '_') {
			return ""
		}
	}
	return h
}
