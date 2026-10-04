package proxy

import (
	"bufio"
	"context"
	"encoding/base64"
	"io"
	"net"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestParse(t *testing.T) {
	cases := []struct {
		in   string
		want Config
		err  bool
	}{
		{"socks5://user:pass@1.2.3.4:1080", Config{KindSOCKS5, "1.2.3.4", 1080, "user", "pass"}, false},
		{"socks5://1.2.3.4:9999", Config{KindSOCKS5, "1.2.3.4", 9999, "", ""}, false},
		{"socks5://example.org", Config{KindSOCKS5, "example.org", 1080, "", ""}, false},
		{"http://u%40x:p%3Aw@proxy.example:3128", Config{KindHTTP, "proxy.example", 3128, "u@x", "p:w"}, false},
		{"http://10.0.0.1", Config{KindHTTP, "10.0.0.1", 8080, "", ""}, false},
		{"ftp://1.2.3.4:21", Config{}, true},
		{"socks5://:1080", Config{}, true},
		{"socks5://1.2.3.4:99999", Config{}, true},
		{"", Config{}, true},
	}
	for _, c := range cases {
		got, err := Parse(c.in)
		if c.err {
			if err == nil {
				t.Errorf("Parse(%q): expected error, got %+v", c.in, got)
			}
			continue
		}
		if err != nil {
			t.Errorf("Parse(%q): unexpected error %v", c.in, err)
			continue
		}
		if got != c.want {
			t.Errorf("Parse(%q) = %+v, want %+v", c.in, got, c.want)
		}
	}
}

func TestRedacted(t *testing.T) {
	cfg, _ := Parse("socks5://user:secret@1.2.3.4:1080")
	if r := cfg.Redacted(); r != "socks5://1.2.3.4:1080" {
		t.Fatalf("Redacted = %q", r)
	}
}

// echoServer echoes everything back on every accepted connection.
func echoServer(t *testing.T) net.Listener {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() {
				defer c.Close()
				io.Copy(c, c)
			}()
		}
	}()
	return ln
}

// fakeSocks5 is a minimal in-process SOCKS5 server used only by tests.
func fakeSocks5(t *testing.T, user, pass string) net.Listener {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go serveSocks5(c, user, pass)
		}
	}()
	return ln
}

func serveSocks5(c net.Conn, user, pass string) {
	defer c.Close()
	var hdr [2]byte
	if _, err := io.ReadFull(c, hdr[:]); err != nil || hdr[0] != 5 {
		return
	}
	methods := make([]byte, int(hdr[1]))
	if _, err := io.ReadFull(c, methods); err != nil {
		return
	}
	if user != "" {
		c.Write([]byte{5, 2})
		var ah [2]byte
		if _, err := io.ReadFull(c, ah[:]); err != nil || ah[0] != 1 {
			return
		}
		u := make([]byte, int(ah[1]))
		io.ReadFull(c, u)
		var pl [1]byte
		io.ReadFull(c, pl[:])
		p := make([]byte, int(pl[0]))
		io.ReadFull(c, p)
		if string(u) != user || string(p) != pass {
			c.Write([]byte{1, 1})
			return
		}
		c.Write([]byte{1, 0})
	} else {
		c.Write([]byte{5, 0})
	}
	var req [4]byte
	if _, err := io.ReadFull(c, req[:]); err != nil || req[1] != 1 {
		return
	}
	var host string
	switch req[3] {
	case 1:
		var ip [4]byte
		io.ReadFull(c, ip[:])
		host = net.IP(ip[:]).String()
	case 3:
		var l [1]byte
		io.ReadFull(c, l[:])
		b := make([]byte, int(l[0]))
		io.ReadFull(c, b)
		host = string(b)
	default:
		c.Write([]byte{5, 8, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	var pb [2]byte
	io.ReadFull(c, pb[:])
	port := int(pb[0])<<8 | int(pb[1])
	up, err := net.DialTimeout("tcp", net.JoinHostPort(host, strconv.Itoa(port)), 2*time.Second)
	if err != nil {
		c.Write([]byte{5, 5, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	defer up.Close()
	c.Write([]byte{5, 0, 0, 1, 127, 0, 0, 1, 0, 0})
	go io.Copy(up, c)
	io.Copy(c, up)
}

// fakeHTTPConnect is a minimal in-process HTTP CONNECT proxy used only by tests.
func fakeHTTPConnect(t *testing.T, user, pass string) net.Listener {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go serveHTTPConnect(c, user, pass)
		}
	}()
	return ln
}

func serveHTTPConnect(c net.Conn, user, pass string) {
	defer c.Close()
	br := bufio.NewReader(c)
	line, err := br.ReadString('\n')
	if err != nil {
		return
	}
	parts := strings.Fields(line)
	if len(parts) < 2 || parts[0] != "CONNECT" {
		io.WriteString(c, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n")
		return
	}
	target := parts[1]
	auth := ""
	for {
		h, err := br.ReadString('\n')
		if err != nil {
			return
		}
		h = strings.TrimRight(h, "\r\n")
		if h == "" {
			break
		}
		if strings.HasPrefix(strings.ToLower(h), "proxy-authorization:") {
			auth = strings.TrimSpace(h[len("proxy-authorization:"):])
		}
	}
	if user != "" {
		want := "Basic " + base64.StdEncoding.EncodeToString([]byte(user+":"+pass))
		if auth != want {
			io.WriteString(c, "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n")
			return
		}
	}
	up, err := net.DialTimeout("tcp", target, 2*time.Second)
	if err != nil {
		io.WriteString(c, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n")
		return
	}
	defer up.Close()
	io.WriteString(c, "HTTP/1.1 200 Connection established\r\n\r\n")
	go io.Copy(up, c)
	io.Copy(c, up)
}

func roundTrip(t *testing.T, d *Dialer, target string) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, err := d.Dial(ctx, target)
	if err != nil {
		t.Fatalf("dial through proxy: %v", err)
	}
	defer conn.Close()
	msg := "hello through proxy"
	if _, err := io.WriteString(conn, msg); err != nil {
		t.Fatal(err)
	}
	buf := make([]byte, len(msg))
	conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	if _, err := io.ReadFull(conn, buf); err != nil {
		t.Fatalf("read echo: %v", err)
	}
	if string(buf) != msg {
		t.Fatalf("echo mismatch: %q", buf)
	}
}

func TestSocks5NoAuth(t *testing.T) {
	echo := echoServer(t)
	defer echo.Close()
	px := fakeSocks5(t, "", "")
	defer px.Close()
	d := NewDialer(Config{Kind: KindSOCKS5, Host: "127.0.0.1", Port: 1}, px.Addr().String(), 3*time.Second)
	roundTrip(t, d, echo.Addr().String())
}

func TestSocks5UserPass(t *testing.T) {
	echo := echoServer(t)
	defer echo.Close()
	px := fakeSocks5(t, "alice", "s3cret")
	defer px.Close()
	d := NewDialer(Config{Kind: KindSOCKS5, Host: "127.0.0.1", Port: 1, Username: "alice", Password: "s3cret"}, px.Addr().String(), 3*time.Second)
	roundTrip(t, d, echo.Addr().String())

	bad := NewDialer(Config{Kind: KindSOCKS5, Host: "127.0.0.1", Port: 1, Username: "alice", Password: "wrong"}, px.Addr().String(), 3*time.Second)
	if _, err := bad.Dial(context.Background(), echo.Addr().String()); err == nil {
		t.Fatal("expected auth failure")
	}
}

func TestSocks5ConnectRefused(t *testing.T) {
	px := fakeSocks5(t, "", "")
	defer px.Close()
	// Take a free port and release it so the upstream connect is refused.
	tmp, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	dead := tmp.Addr().String()
	tmp.Close()
	d := NewDialer(Config{Kind: KindSOCKS5, Host: "127.0.0.1", Port: 1}, px.Addr().String(), 3*time.Second)
	if _, err := d.Dial(context.Background(), dead); err == nil {
		t.Fatal("expected connect failure")
	}
}

func TestHTTPConnectNoAuth(t *testing.T) {
	echo := echoServer(t)
	defer echo.Close()
	px := fakeHTTPConnect(t, "", "")
	defer px.Close()
	d := NewDialer(Config{Kind: KindHTTP, Host: "127.0.0.1", Port: 1}, px.Addr().String(), 3*time.Second)
	roundTrip(t, d, echo.Addr().String())
}

func TestHTTPConnectBasicAuth(t *testing.T) {
	echo := echoServer(t)
	defer echo.Close()
	px := fakeHTTPConnect(t, "bob", "pw")
	defer px.Close()
	d := NewDialer(Config{Kind: KindHTTP, Host: "127.0.0.1", Port: 1, Username: "bob", Password: "pw"}, px.Addr().String(), 3*time.Second)
	roundTrip(t, d, echo.Addr().String())

	bad := NewDialer(Config{Kind: KindHTTP, Host: "127.0.0.1", Port: 1, Username: "bob", Password: "nope"}, px.Addr().String(), 3*time.Second)
	if _, err := bad.Dial(context.Background(), echo.Addr().String()); err == nil {
		t.Fatal("expected 407 failure")
	}
}
