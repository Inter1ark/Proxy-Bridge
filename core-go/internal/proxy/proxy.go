// Package proxy parses upstream proxy URLs and dials TCP connections
// through a SOCKS5 or HTTP CONNECT proxy.
package proxy

import (
	"bufio"
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Kind is the upstream proxy protocol.
type Kind string

const (
	KindSOCKS5 Kind = "socks5"
	KindHTTP   Kind = "http"
)

// Config describes an upstream proxy.
type Config struct {
	Kind     Kind
	Host     string // hostname or IP as written in the URL
	Port     int
	Username string
	Password string
}

// Addr returns host:port.
func (c Config) Addr() string {
	return net.JoinHostPort(c.Host, strconv.Itoa(c.Port))
}

// Redacted returns the proxy URL without credentials, e.g. socks5://host:port.
func (c Config) Redacted() string {
	return fmt.Sprintf("%s://%s", c.Kind, c.Addr())
}

// Parse parses socks5://[user:pass@]host:port or http://[user:pass@]host:port.
func Parse(raw string) (Config, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return Config{}, errors.New("proxy URL is empty")
	}
	u, err := url.Parse(raw)
	if err != nil {
		return Config{}, fmt.Errorf("invalid proxy URL: %w", err)
	}
	var cfg Config
	switch strings.ToLower(u.Scheme) {
	case "socks5", "socks5h", "socks":
		cfg.Kind = KindSOCKS5
	case "http", "https":
		cfg.Kind = KindHTTP
	default:
		return Config{}, fmt.Errorf("unsupported proxy scheme %q (use socks5:// or http://)", u.Scheme)
	}
	cfg.Host = u.Hostname()
	if cfg.Host == "" {
		return Config{}, errors.New("proxy host is empty")
	}
	portStr := u.Port()
	if portStr == "" {
		if cfg.Kind == KindSOCKS5 {
			portStr = "1080"
		} else {
			portStr = "8080"
		}
	}
	port, err := strconv.Atoi(portStr)
	if err != nil || port < 1 || port > 65535 {
		return Config{}, fmt.Errorf("invalid proxy port %q", portStr)
	}
	cfg.Port = port
	if u.User != nil {
		cfg.Username = u.User.Username()
		cfg.Password, _ = u.User.Password()
	}
	return cfg, nil
}

// Dialer opens TCP connections to arbitrary targets through the proxy.
type Dialer struct {
	cfg Config
	// serverAddr is the resolved ip:port of the proxy server. It is used instead
	// of cfg.Host so that no DNS lookup happens after routing is changed.
	serverAddr string
	timeout    time.Duration
}

// NewDialer creates a dialer. serverAddr must be the resolved ip:port of the proxy.
func NewDialer(cfg Config, serverAddr string, timeout time.Duration) *Dialer {
	if timeout <= 0 {
		timeout = 15 * time.Second
	}
	return &Dialer{cfg: cfg, serverAddr: serverAddr, timeout: timeout}
}

// Dial connects to target (ip:port or host:port) through the proxy.
func (d *Dialer) Dial(ctx context.Context, target string) (net.Conn, error) {
	nd := net.Dialer{Timeout: d.timeout}
	conn, err := nd.DialContext(ctx, "tcp", d.serverAddr)
	if err != nil {
		return nil, fmt.Errorf("connect to proxy %s: %w", d.serverAddr, err)
	}
	_ = conn.SetDeadline(time.Now().Add(d.timeout))
	switch d.cfg.Kind {
	case KindSOCKS5:
		err = socks5Handshake(conn, d.cfg, target)
	case KindHTTP:
		err = httpConnect(conn, d.cfg, target)
	default:
		err = fmt.Errorf("unknown proxy kind %q", d.cfg.Kind)
	}
	if err != nil {
		conn.Close()
		return nil, err
	}
	_ = conn.SetDeadline(time.Time{})
	return conn, nil
}

// SOCKS5 constants (RFC 1928 CONNECT, RFC 1929 username/password).
const (
	socksVersion     = 0x05
	socksNoAuth      = 0x00
	socksUserPass    = 0x02
	socksNoAccept    = 0xFF
	socksCmdConnect  = 0x01
	socksAtypIPv4    = 0x01
	socksAtypDomain  = 0x03
	socksAtypIPv6    = 0x04
	socksAuthVersion = 0x01
)

func socks5Handshake(conn net.Conn, cfg Config, target string) error {
	host, portStr, err := net.SplitHostPort(target)
	if err != nil {
		return fmt.Errorf("bad target %q: %w", target, err)
	}
	port, err := strconv.Atoi(portStr)
	if err != nil || port < 0 || port > 65535 {
		return fmt.Errorf("bad target port %q", portStr)
	}

	useAuth := cfg.Username != "" || cfg.Password != ""
	greeting := []byte{socksVersion, 1, socksNoAuth}
	if useAuth {
		greeting = []byte{socksVersion, 2, socksNoAuth, socksUserPass}
	}
	if _, err := conn.Write(greeting); err != nil {
		return fmt.Errorf("socks5 greeting: %w", err)
	}
	var resp [2]byte
	if _, err := io.ReadFull(conn, resp[:]); err != nil {
		return fmt.Errorf("socks5 greeting reply: %w", err)
	}
	if resp[0] != socksVersion {
		return fmt.Errorf("socks5: unexpected version %d", resp[0])
	}
	switch resp[1] {
	case socksNoAuth:
	case socksUserPass:
		if !useAuth {
			return errors.New("socks5: proxy requires username/password")
		}
		if len(cfg.Username) > 255 || len(cfg.Password) > 255 {
			return errors.New("socks5: username or password too long")
		}
		buf := make([]byte, 0, 3+len(cfg.Username)+len(cfg.Password))
		buf = append(buf, socksAuthVersion, byte(len(cfg.Username)))
		buf = append(buf, cfg.Username...)
		buf = append(buf, byte(len(cfg.Password)))
		buf = append(buf, cfg.Password...)
		if _, err := conn.Write(buf); err != nil {
			return fmt.Errorf("socks5 auth: %w", err)
		}
		var ar [2]byte
		if _, err := io.ReadFull(conn, ar[:]); err != nil {
			return fmt.Errorf("socks5 auth reply: %w", err)
		}
		if ar[1] != 0x00 {
			return errors.New("socks5: authentication failed")
		}
	case socksNoAccept:
		return errors.New("socks5: no acceptable authentication method")
	default:
		return fmt.Errorf("socks5: unsupported auth method %d", resp[1])
	}

	req := []byte{socksVersion, socksCmdConnect, 0x00}
	if ip := net.ParseIP(host); ip != nil {
		if ip4 := ip.To4(); ip4 != nil {
			req = append(req, socksAtypIPv4)
			req = append(req, ip4...)
		} else {
			req = append(req, socksAtypIPv6)
			req = append(req, ip.To16()...)
		}
	} else {
		if len(host) > 255 {
			return errors.New("socks5: hostname too long")
		}
		req = append(req, socksAtypDomain, byte(len(host)))
		req = append(req, host...)
	}
	req = append(req, byte(port>>8), byte(port))
	if _, err := conn.Write(req); err != nil {
		return fmt.Errorf("socks5 connect: %w", err)
	}

	var head [4]byte
	if _, err := io.ReadFull(conn, head[:]); err != nil {
		return fmt.Errorf("socks5 connect reply: %w", err)
	}
	if head[0] != socksVersion {
		return fmt.Errorf("socks5: bad reply version %d", head[0])
	}
	if head[1] != 0x00 {
		return fmt.Errorf("socks5: connect failed: %s", socksReplyText(head[1]))
	}
	var skip int
	switch head[3] {
	case socksAtypIPv4:
		skip = 4
	case socksAtypIPv6:
		skip = 16
	case socksAtypDomain:
		var l [1]byte
		if _, err := io.ReadFull(conn, l[:]); err != nil {
			return fmt.Errorf("socks5 reply addr: %w", err)
		}
		skip = int(l[0])
	default:
		return fmt.Errorf("socks5: bad reply address type %d", head[3])
	}
	if _, err := io.CopyN(io.Discard, conn, int64(skip+2)); err != nil {
		return fmt.Errorf("socks5 reply addr: %w", err)
	}
	return nil
}

func socksReplyText(code byte) string {
	switch code {
	case 0x01:
		return "general failure"
	case 0x02:
		return "connection not allowed by ruleset"
	case 0x03:
		return "network unreachable"
	case 0x04:
		return "host unreachable"
	case 0x05:
		return "connection refused"
	case 0x06:
		return "TTL expired"
	case 0x07:
		return "command not supported"
	case 0x08:
		return "address type not supported"
	}
	return fmt.Sprintf("reply code %d", code)
}

func httpConnect(conn net.Conn, cfg Config, target string) error {
	var sb strings.Builder
	fmt.Fprintf(&sb, "CONNECT %s HTTP/1.1\r\nHost: %s\r\n", target, target)
	if cfg.Username != "" || cfg.Password != "" {
		cred := base64.StdEncoding.EncodeToString([]byte(cfg.Username + ":" + cfg.Password))
		fmt.Fprintf(&sb, "Proxy-Authorization: Basic %s\r\n", cred)
	}
	sb.WriteString("Proxy-Connection: Keep-Alive\r\n\r\n")
	if _, err := io.WriteString(conn, sb.String()); err != nil {
		return fmt.Errorf("http connect: %w", err)
	}
	br := bufio.NewReader(conn)
	resp, err := http.ReadResponse(br, &http.Request{Method: http.MethodConnect})
	if err != nil {
		return fmt.Errorf("http connect reply: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("http connect: proxy answered %s", resp.Status)
	}
	if br.Buffered() > 0 {
		// A successful CONNECT reply carries no body, so anything already
		// buffered would belong to the tunnel and would be lost. Refuse it.
		return errors.New("http connect: unexpected data after response")
	}
	return nil
}
