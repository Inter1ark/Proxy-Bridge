package engine

import (
	"crypto/tls"
	"net"
	"testing"
	"time"
)

// clientHello captures the first bytes a Go TLS client sends for serverName.
func clientHello(t *testing.T, serverName string) []byte {
	t.Helper()
	a, b := net.Pipe()
	go func() {
		c := tls.Client(a, &tls.Config{ServerName: serverName, InsecureSkipVerify: serverName == ""})
		_ = c.SetDeadline(time.Now().Add(2 * time.Second))
		_ = c.Handshake()
		a.Close()
	}()
	_ = b.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 0, 8192)
	tmp := make([]byte, 8192)
	for {
		n, err := b.Read(tmp)
		buf = append(buf, tmp[:n]...)
		if h, more := parseHost(buf); h != "" || !more || err != nil {
			break
		}
	}
	b.Close()
	return buf
}

func TestSniffTLSServerName(t *testing.T) {
	hello := clientHello(t, "www.example.com")
	if h, _ := parseHost(hello); h != "www.example.com" {
		t.Fatalf("got %q", h)
	}
	// a ClientHello that arrives in two TCP segments
	if h, more := parseHost(hello[:40]); h != "" || !more {
		t.Fatalf("partial hello: host %q more %v, want more data", h, more)
	}
}

func TestSniffTLSWithoutServerName(t *testing.T) {
	hello := clientHello(t, "")
	if h, more := parseHost(hello); h != "" || more {
		t.Fatalf("got %q more=%v, want no name", h, more)
	}
}

func TestSniffHTTPHost(t *testing.T) {
	req := []byte("GET /path HTTP/1.1\r\nUser-Agent: x\r\nHost: Example.org:8080\r\nAccept: */*\r\n\r\n")
	if h, _ := parseHost(req); h != "example.org" {
		t.Fatalf("got %q", h)
	}
	if h, more := parseHost([]byte("GE")); h != "" || !more {
		t.Fatalf("two bytes of GET: host %q more %v", h, more)
	}
	if h, more := parseHost([]byte("GET / HTTP/1.1\r\nUser-Agent: x\r\n")); h != "" || !more {
		t.Fatalf("headers not finished: host %q more %v", h, more)
	}
}

func TestSniffIgnoresOtherProtocolsAndAddresses(t *testing.T) {
	if h, more := parseHost([]byte("SSH-2.0-OpenSSH_9.6\r\n")); h != "" || more {
		t.Fatalf("ssh: %q %v", h, more)
	}
	for _, bad := range []string{"1.2.3.4", "[::1]", "localhost", "exa mple.com", ""} {
		if cleanHost(bad) != "" {
			t.Errorf("cleanHost(%q) must reject it", bad)
		}
	}
	if cleanHost("Sub.Example.COM.") != "sub.example.com" {
		t.Error("cleanHost must lower-case and drop the trailing dot")
	}
}

func TestSniffHostReadsUntilTheNameOrTheWait(t *testing.T) {
	a, b := net.Pipe()
	hello := clientHello(t, "api.example.net")
	go func() {
		_, _ = a.Write(hello[:100])
		time.Sleep(30 * time.Millisecond)
		_, _ = a.Write(hello[100:])
	}()
	first, host := sniffHost(b)
	if host != "api.example.net" || len(first) != len(hello) {
		t.Fatalf("host %q, %d of %d bytes", host, len(first), len(hello))
	}
	a.Close()
	b.Close()

	// a server-first protocol: nothing comes, the wait ends and the flow goes by address
	c, d := net.Pipe()
	start := time.Now()
	first, host = sniffHost(d)
	if host != "" || len(first) != 0 || time.Since(start) > sniffWait+300*time.Millisecond {
		t.Fatalf("silent client: host %q, %d bytes, %v", host, len(first), time.Since(start))
	}
	c.Close()
	d.Close()
}
