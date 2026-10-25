//go:build darwin || linux

package main

import (
	"net/netip"
	"os"
	"proxybridge/core/internal/tunnel"
	"testing"
	"time"
)

func TestParseStartRequest(t *testing.T) {
	defaults := tunnel.Config{DNS: netip.MustParseAddr("1.1.1.1"), MTU: 1500, StatsEvery: time.Second}
	payload := []byte(`{"proxies":[{"id":1,"url":"socks5://u:p@1.2.3.4:1080"}],"rules":[{"process":"*","action":"proxy","proxy":1}],"parent_pid":4242,"exempt_pids":[7,8],"dns":"8.8.8.8"}`)
	cfg, err := tunnel.ParseRequest(payload, defaults)
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg.Set.Rules) != 1 || len(cfg.Set.Proxies) != 1 || cfg.ParentPID != 4242 {
		t.Fatalf("cfg %+v", cfg)
	}
	if cfg.DNS != netip.MustParseAddr("8.8.8.8") || cfg.MTU != 1500 {
		t.Fatalf("defaults not applied: %+v", cfg)
	}
	want := map[int]bool{os.Getpid(): true, 7: true, 8: true, 4242: true}
	for _, pid := range cfg.Exempt {
		delete(want, pid)
	}
	if len(want) != 0 {
		t.Fatalf("exempt pids missing: %v (got %v)", want, cfg.Exempt)
	}

	// no dns field: the daemon default stays; empty dns: dropped
	cfg, err = tunnel.ParseRequest([]byte(`{"proxies":[{"id":1,"url":"http://h:1"}],"rules":[{"process":"*","action":"proxy","proxy":1}]}`), defaults)
	if err != nil || cfg.DNS != defaults.DNS {
		t.Fatalf("default dns: %v %+v", err, cfg)
	}
	cfg, err = tunnel.ParseRequest([]byte(`{"proxies":[{"id":1,"url":"http://h:1"}],"rules":[{"process":"*","action":"proxy","proxy":1}],"dns":""}`), defaults)
	if err != nil || cfg.DNS.IsValid() {
		t.Fatalf("empty dns: %v %+v", err, cfg)
	}
	if _, err := tunnel.ParseRequest([]byte(`{"rules":[{"process":"*","action":"proxy","proxy":9}]}`), defaults); err == nil {
		t.Fatal("unknown proxy id must be rejected")
	}
	if _, err := tunnel.ParseRequest([]byte(`{"dns":"nope","proxies":[{"id":1,"url":"http://h:1"}],"rules":[]}`), defaults); err == nil {
		t.Fatal("bad dns must be rejected")
	}
}

func TestResolveToken(t *testing.T) {
	dir := t.TempDir()
	path := dir + "/control.token"
	if tok, err := resolveToken("flag", path, true); err != nil || tok != "flag" {
		t.Fatalf("flag: %q %v", tok, err)
	}
	if _, err := resolveToken("", path, false); err == nil {
		t.Fatal("missing file must fail outside daemon mode")
	}
	tok, err := resolveToken("", path, true)
	if err != nil {
		if os.Geteuid() != 0 {
			t.Skipf("token file needs chown root: %v", err)
		}
		t.Fatal(err)
	}
	if len(tok) != 32 {
		t.Fatalf("token %q", tok)
	}
	again, err := resolveToken("", path, true)
	if err != nil || again != tok {
		t.Fatalf("second read: %q %v", again, err)
	}
}

func TestParseStartRequestUDP(t *testing.T) {
	defaults := tunnel.Config{DNS: netip.MustParseAddr("1.1.1.1"), MTU: 1500, StatsEvery: time.Second}
	base := `"proxies":[{"id":1,"url":"socks5://u:p@1.2.3.4:1080"},{"id":2,"url":"http://h:8080"}],"rules":[{"process":"*","action":"proxy","proxy":1}]`
	cfg, err := tunnel.ParseRequest([]byte(`{`+base+`}`), defaults)
	if err != nil || cfg.UDP.Mode != tunnel.UDPDirect || cfg.UDP.DNSViaProxy {
		t.Fatalf("defaults: %v %+v", err, cfg.UDP)
	}
	cfg, err = tunnel.ParseRequest([]byte(`{`+base+`,"udp":"proxy","dns_via_proxy":true,"dns_proxy":2}`), defaults)
	if err != nil || cfg.UDP.Mode != tunnel.UDPProxy || !cfg.UDP.DNSViaProxy || cfg.UDP.DNSProxy != 2 {
		t.Fatalf("options: %v %+v", err, cfg.UDP)
	}
	cfg, err = tunnel.ParseRequest([]byte(`{`+base+`,"udp":"block","dns_proxy":9}`), defaults)
	if err != nil || cfg.UDP.Mode != tunnel.UDPBlock || cfg.UDP.DNSProxy != 0 {
		t.Fatalf("unknown dns proxy must become automatic: %v %+v", err, cfg.UDP)
	}
	if _, err := tunnel.ParseRequest([]byte(`{`+base+`,"udp":"maybe"}`), defaults); err == nil {
		t.Fatal("bad udp mode must be rejected")
	}
}
