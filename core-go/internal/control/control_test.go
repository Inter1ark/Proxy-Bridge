package control

import (
	"bufio"
	"net"
	"strings"
	"testing"
	"time"

	"proxybridge/core/internal/status"
)

func send(t *testing.T, addr, line string) string {
	t.Helper()
	c, err := net.DialTimeout("tcp", addr, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(2 * time.Second))
	if _, err := c.Write([]byte(line + "\n")); err != nil {
		t.Fatal(err)
	}
	reply, _ := bufio.NewReader(c).ReadString('\n')
	return strings.TrimSpace(reply)
}

func TestControlCommands(t *testing.T) {
	stopped := make(chan struct{}, 1)
	rep := status.New()
	srv, err := Listen(0, "tok123", rep, func() { stopped <- struct{}{} }, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer srv.Close()
	addr := srv.Addr()

	if got := send(t, addr, "ping tok123"); got != "ok" {
		t.Fatalf("ping: %q", got)
	}
	if got := send(t, addr, "ping wrong"); got != "error unauthorized" {
		t.Fatalf("bad token: %q", got)
	}
	if got := send(t, addr, "frobnicate tok123"); got != "error unknown command" {
		t.Fatalf("unknown: %q", got)
	}
	if got := send(t, addr, "stop tok123"); got != "ok" {
		t.Fatalf("stop: %q", got)
	}
	select {
	case <-stopped:
	case <-time.After(2 * time.Second):
		t.Fatal("stop callback not invoked")
	}
}

func TestWatchStreamsStatus(t *testing.T) {
	rep := status.New()
	srv, err := Listen(0, "tok", rep, func() {}, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer srv.Close()

	rep.Up("utun9", "socks5://1.2.3.4:1080")

	c, err := net.DialTimeout("tcp", srv.Addr(), 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(3 * time.Second))
	c.Write([]byte("watch tok\n"))
	r := bufio.NewReader(c)

	// The last state line is replayed to a late watcher.
	first, err := r.ReadString('\n')
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(first, `"status":"up"`) || !strings.Contains(first, `"tun":"utun9"`) {
		t.Fatalf("unexpected replay: %q", first)
	}

	// Wait until the watcher is attached, then publish a stats line.
	deadline := time.Now().Add(2 * time.Second)
	for {
		rep.Stats(10, 20)
		c.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
		line, err := r.ReadString('\n')
		if err == nil {
			if !strings.Contains(line, `"status":"stats"`) || !strings.Contains(line, `"up":10`) {
				t.Fatalf("unexpected stats line: %q", line)
			}
			return
		}
		if time.Now().After(deadline) {
			t.Fatal("stats line never arrived")
		}
	}
}

func TestRulesCommand(t *testing.T) {
	rep := status.New()
	var got []byte
	srv, err := Listen(0, "tok", rep, func() {}, func(b []byte) error {
		got = b
		if string(b) == "bad" {
			return errorf("rejected")
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	defer srv.Close()
	if r := send(t, srv.Addr(), "rules tok eyJhIjoxfQ=="); r != "ok" {
		t.Fatalf("rules: %q", r)
	}
	if string(got) != `{"a":1}` {
		t.Fatalf("payload %q", got)
	}
	if r := send(t, srv.Addr(), "rules tok YmFk"); r != "error rejected" {
		t.Fatalf("bad rules: %q", r)
	}
	if r := send(t, srv.Addr(), "rules tok"); !strings.HasPrefix(r, "error usage") {
		t.Fatalf("usage: %q", r)
	}
	if r := send(t, srv.Addr(), "rules wrong eyJhIjoxfQ=="); r != "error unauthorized" {
		t.Fatalf("auth: %q", r)
	}
}

type simpleError string

func (e simpleError) Error() string { return string(e) }

func errorf(s string) error { return simpleError(s) }
