//go:build darwin || linux

package tunnel

import (
	"net/netip"
	"testing"

	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/rules"
)

// A process without a rule of its own follows the nearest ancestor that has one.
func TestMatchTree(t *testing.T) {
	set := &rules.Set{
		Proxies: []rules.Proxy{{ID: 1, URL: "socks5://a:1"}, {ID: 2, URL: "socks5://b:1"}, {ID: 3, URL: "socks5://c:1"}},
		Rules: []rules.Rule{
			{Process: "/Applications/Codex.app", Action: rules.ActionProxy, Proxy: 2},
			{Process: "curl", Action: rules.ActionProxy, Proxy: 3},
			{Process: "/Applications/Terminal.app", Action: rules.ActionDirect},
			{Process: "*", Action: rules.ActionProxy, Proxy: 1},
		},
	}
	// pid -> (ppid, exe)
	tree := map[int][2]any{
		300: {200, "/Applications/Codex.app/Contents/MacOS/Codex"},
		200: {100, "/Applications/Terminal.app/Contents/MacOS/Terminal"},
		100: {1, "/sbin/launchd"},
		500: {400, "/bin/zsh"},
		400: {1, "/usr/libexec/something"},
	}
	parent := func(pid int) (int, string, bool) {
		p, ok := tree[pid]
		if !ok {
			return 0, "", false
		}
		return p[0].(int), p[1].(string), true
	}
	// node started by Codex: Codex's proxy, not the catch-all
	r, ok, via := matchTree(set, 301, "/usr/local/bin/node", func(pid int) (int, string, bool) {
		if pid == 301 {
			return 300, "/Applications/Codex.app/Contents/MacOS/Codex", true
		}
		return parent(pid)
	})
	if !ok || r.Proxy != 2 || via == "" {
		t.Fatalf("child of Codex: %+v ok=%v via=%q", r, ok, via)
	}
	// own specific rule wins over the ancestor's
	r, ok, via = matchTree(set, 301, "/usr/bin/curl", func(pid int) (int, string, bool) {
		return 300, "/Applications/Codex.app/Contents/MacOS/Codex", true
	})
	if !ok || r.Proxy != 3 || via != "" {
		t.Fatalf("own rule: %+v via=%q", r, via)
	}
	// the nearest ancestor wins: Codex (proxy 2) before Terminal (direct)
	r, ok, _ = matchTree(set, 300, "/Applications/Codex.app/Contents/MacOS/Codex", parent)
	if !ok || r.Proxy != 2 {
		t.Fatalf("Codex itself: %+v", r)
	}
	r, ok, via = matchTree(set, 310, "/usr/local/bin/node", func(pid int) (int, string, bool) {
		if pid == 310 {
			return 200, "/Applications/Terminal.app/Contents/MacOS/Terminal", true
		}
		return parent(pid)
	})
	if !ok || r.Action != rules.ActionDirect || via == "" {
		t.Fatalf("child of Terminal: %+v via=%q", r, via)
	}
	// no ancestor with a rule: the catch-all
	r, ok, via = matchTree(set, 500, "/usr/bin/python3", parent)
	if !ok || r.Proxy != 1 || via != "" {
		t.Fatalf("catch-all: %+v via=%q", r, via)
	}
	// unknown process (pid 0, empty exe): the catch-all, no walk
	calls := 0
	r, ok, _ = matchTree(set, 0, "", func(int) (int, string, bool) { calls++; return 0, "", false })
	if !ok || r.Proxy != 1 || calls != 0 {
		t.Fatalf("unknown process: %+v calls=%d", r, calls)
	}
	// without a catch-all an unrelated process stays unmatched
	set.Rules = set.Rules[:3]
	if _, ok, _ = matchTree(set, 500, "/usr/bin/python3", parent); ok {
		t.Fatal("matched without any applicable rule")
	}
	// the walk is bounded
	calls = 0
	_, ok, _ = matchTree(set, 1000, "/bin/x", func(pid int) (int, string, bool) { calls++; return pid + 1, "/bin/y", true })
	if ok || calls != inheritDepth {
		t.Fatalf("depth: ok=%v calls=%d", ok, calls)
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
