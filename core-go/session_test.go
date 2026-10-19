//go:build darwin || linux

package main

import (
	"net/netip"
	"os"
	"testing"
	"time"

	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/rules"
)

func TestParseStartRequest(t *testing.T) {
	defaults := sessionConfig{DNS: netip.MustParseAddr("1.1.1.1"), MTU: 1500, StatsEvery: time.Second}
	payload := []byte(`{"proxies":[{"id":1,"url":"socks5://u:p@1.2.3.4:1080"}],"rules":[{"process":"*","action":"proxy","proxy":1}],"parent_pid":4242,"exempt_pids":[7,8],"dns":"8.8.8.8"}`)
	cfg, err := parseStartRequest(payload, defaults)
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
	cfg, err = parseStartRequest([]byte(`{"proxies":[{"id":1,"url":"http://h:1"}],"rules":[{"process":"*","action":"proxy","proxy":1}]}`), defaults)
	if err != nil || cfg.DNS != defaults.DNS {
		t.Fatalf("default dns: %v %+v", err, cfg)
	}
	cfg, err = parseStartRequest([]byte(`{"proxies":[{"id":1,"url":"http://h:1"}],"rules":[{"process":"*","action":"proxy","proxy":1}],"dns":""}`), defaults)
	if err != nil || cfg.DNS.IsValid() {
		t.Fatalf("empty dns: %v %+v", err, cfg)
	}
	if _, err := parseStartRequest([]byte(`{"rules":[{"process":"*","action":"proxy","proxy":9}]}`), defaults); err == nil {
		t.Fatal("unknown proxy id must be rejected")
	}
	if _, err := parseStartRequest([]byte(`{"dns":"nope","proxies":[{"id":1,"url":"http://h:1"}],"rules":[]}`), defaults); err == nil {
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
	defaults := sessionConfig{DNS: netip.MustParseAddr("1.1.1.1"), MTU: 1500, StatsEvery: time.Second}
	base := `"proxies":[{"id":1,"url":"socks5://u:p@1.2.3.4:1080"},{"id":2,"url":"http://h:8080"}],"rules":[{"process":"*","action":"proxy","proxy":1}]`
	cfg, err := parseStartRequest([]byte(`{`+base+`}`), defaults)
	if err != nil || cfg.UDP.Mode != udpDirect || cfg.UDP.DNSViaProxy {
		t.Fatalf("defaults: %v %+v", err, cfg.UDP)
	}
	cfg, err = parseStartRequest([]byte(`{`+base+`,"udp":"proxy","dns_via_proxy":true,"dns_proxy":2}`), defaults)
	if err != nil || cfg.UDP.Mode != udpProxy || !cfg.UDP.DNSViaProxy || cfg.UDP.DNSProxy != 2 {
		t.Fatalf("options: %v %+v", err, cfg.UDP)
	}
	cfg, err = parseStartRequest([]byte(`{`+base+`,"udp":"block","dns_proxy":9}`), defaults)
	if err != nil || cfg.UDP.Mode != udpBlock || cfg.UDP.DNSProxy != 0 {
		t.Fatalf("unknown dns proxy must become automatic: %v %+v", err, cfg.UDP)
	}
	if _, err := parseStartRequest([]byte(`{`+base+`,"udp":"maybe"}`), defaults); err == nil {
		t.Fatal("bad udp mode must be rejected")
	}
}

func TestDNSProxyChoice(t *testing.T) {
	set := &rules.Set{
		Proxies: []rules.Proxy{{ID: 1, URL: "socks5://a:1"}, {ID: 2, URL: "socks5://b:1"}},
		Rules:   []rules.Rule{{Process: "curl", Action: rules.ActionProxy, Proxy: 2}, {Process: "*", Action: rules.ActionDirect}},
	}
	t1 := &ruleTable{set: set, dialers: map[int]*proxy.Dialer{1: {}, 2: {}}}
	if got := t1.dnsProxyChoice(0); got != 2 {
		t.Fatalf("first PROXY rule: %d", got)
	}
	if got := t1.dnsProxyChoice(1); got != 1 {
		t.Fatalf("explicit: %d", got)
	}
	if got := t1.dnsProxyChoice(7); got != 2 {
		t.Fatalf("unknown explicit: %d", got)
	}
	set.Rules = append([]rules.Rule{{Process: "*", Action: rules.ActionProxy, Proxy: 1}}, set.Rules...)
	if got := t1.dnsProxyChoice(0); got != 1 {
		t.Fatalf("catch-all first: %d", got)
	}
	if !isPrivate(netip.MustParseAddr("192.168.64.1")) || !isPrivate(netip.MustParseAddr("100.64.1.1")) || isPrivate(netip.MustParseAddr("1.1.1.1")) {
		t.Fatal("isPrivate")
	}
}
