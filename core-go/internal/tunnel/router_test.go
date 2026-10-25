//go:build darwin || linux

package tunnel

import (
	"net/netip"
	"testing"

	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/rules"
)

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
