//go:build darwin || linux

package main

import (
	"context"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"proxybridge/core/internal/engine"
	"proxybridge/core/internal/netcfg"
	"proxybridge/core/internal/procmap"
	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/rules"
)

// router decides, for every new TCP flow, whether it goes through a proxy,
// directly, or nowhere. It owns the current rule set (replaceable while
// running) and one dialer per proxy.
type router struct {
	log     logger
	procs   *procmap.Resolver
	direct  *net.Dialer
	base    *net.Dialer
	gateway netcfg.Gateway
	undo    *netcfg.Undo
	exempt  map[int]bool

	mu      sync.Mutex
	routed  map[netip.Addr]bool // proxy IPs that already have a host route
	current atomic.Pointer[ruleTable]

	// unknownLogged rate-limits the "owner unknown" log line.
	unknownLogged atomic.Int64
}

// ruleTable is an immutable, resolved rule set.
type ruleTable struct {
	set     *rules.Set
	dialers map[int]*proxy.Dialer
	// summary is shown in the "up" status line: the catch-all proxy or "direct".
	summary string
}

func newRouter(log logger, gw netcfg.Gateway, undo *netcfg.Undo, exempt []int) *router {
	base := boundDialer(gw.Interface)
	r := &router{
		log:     log,
		direct:  base,
		base:    base,
		gateway: gw,
		undo:    undo,
		exempt:  make(map[int]bool, len(exempt)),
		routed:  make(map[netip.Addr]bool),
	}
	for _, pid := range exempt {
		if pid > 0 {
			r.exempt[pid] = true
		}
	}
	r.procs = procmap.New(log.Infof)
	return r
}

// Procmap names the process attribution method for the status line.
func (r *router) Procmap() string {
	if r.procs.UsesLsof() {
		return "lsof"
	}
	return "pcblist"
}

// Load resolves the proxies of a set, pins their IPs to the real gateway and
// makes the set current. Flows already open are not affected.
func (r *router) Load(set *rules.Set) error {
	if err := set.Validate(); err != nil {
		return err
	}
	t := &ruleTable{set: set, dialers: make(map[int]*proxy.Dialer, len(set.Proxies)), summary: "direct"}
	for _, p := range set.Proxies {
		cfg, err := proxy.Parse(p.URL)
		if err != nil {
			return fmt.Errorf("proxy %d: %w", p.ID, err)
		}
		ip, err := r.resolveIPv4(cfg.Host)
		if err != nil {
			return fmt.Errorf("proxy %d: resolve %q: %w", p.ID, cfg.Host, err)
		}
		if err := r.pin(ip); err != nil {
			return fmt.Errorf("proxy %d: host route for %s: %w", p.ID, ip, err)
		}
		serverAddr := net.JoinHostPort(ip.String(), strconv.Itoa(cfg.Port))
		t.dialers[p.ID] = proxy.NewDialerWith(cfg, serverAddr, 15*time.Second, r.base)
		r.log.Infof("proxy %d: %s -> %s", p.ID, cfg.Redacted(), serverAddr)
	}
	for _, rule := range set.Rules {
		if rule.Process == "*" {
			if rule.Action == rules.ActionProxy {
				t.summary = t.dialers[rule.Proxy].Config().Redacted()
			} else {
				t.summary = string(rule.Action)
			}
			break
		}
	}
	r.current.Store(t)
	for i, rule := range set.Rules {
		target := string(rule.Action)
		if rule.Action == rules.ActionProxy {
			target = "proxy " + strconv.Itoa(rule.Proxy)
		}
		r.log.Infof("rule %d: %s -> %s", i+1, rule.Process, target)
	}
	return nil
}

// Table returns the current rule set (never nil after Load).
func (r *router) Table() *ruleTable { return r.current.Load() }

// Summary describes where "all other programs" go.
func (r *router) Summary() string {
	if t := r.current.Load(); t != nil {
		return t.summary
	}
	return "direct"
}

// Decide implements engine.RouteFunc.
func (r *router) Decide(src, dst netip.AddrPort) engine.Decision {
	t := r.current.Load()
	if t == nil {
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	}
	pid, exe, known := r.procs.Lookup(src, dst)
	if known && r.exempt[pid] {
		r.log.Debugf("tcp %s -> %s: pid %d is exempt: direct", src, dst, pid)
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	}
	if !known {
		now := time.Now().Unix()
		if last := r.unknownLogged.Load(); now-last >= 10 && r.unknownLogged.CompareAndSwap(last, now) {
			r.log.Infof("tcp %s -> %s: owner process unknown, using the catch-all rule", src, dst)
		}
	}
	rule, ok := t.set.Match(exe)
	if !ok {
		r.log.Debugf("tcp %s -> %s: pid %d %q: no rule: direct", src, dst, pid, exe)
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	}
	switch rule.Action {
	case rules.ActionBlock:
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q: block", src, dst, pid, exe, rule.Process)
		return engine.Decision{Block: true, Label: "block"}
	case rules.ActionDirect:
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q: direct", src, dst, pid, exe, rule.Process)
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	default:
		d := t.dialers[rule.Proxy]
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q: proxy %d", src, dst, pid, exe, rule.Process, rule.Proxy)
		return engine.Decision{Dial: d.Dial, Label: "proxy " + strconv.Itoa(rule.Proxy)}
	}
}

// directDial opens the destination outside the tunnel.
func (r *router) directDial(ctx context.Context, target string) (net.Conn, error) {
	d := *r.direct
	d.Timeout = 15 * time.Second
	return d.DialContext(ctx, "tcp", target)
}

func (r *router) pin(ip netip.Addr) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.routed[ip] {
		return nil
	}
	if err := netcfg.AddHostRoute(ip, r.gateway, r.undo); err != nil {
		return err
	}
	r.routed[ip] = true
	return nil
}

// resolveIPv4 resolves a proxy host name through the real interface, so it
// works both before the tunnel is up and while it is running.
func (r *router) resolveIPv4(host string) (netip.Addr, error) {
	if ip, err := netip.ParseAddr(host); err == nil {
		if !ip.Is4() {
			return netip.Addr{}, fmt.Errorf("IPv6 proxy addresses are not supported")
		}
		return ip, nil
	}
	res := &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, address string) (net.Conn, error) {
			d := *r.base
			d.Timeout = 5 * time.Second
			return d.DialContext(ctx, network, address)
		},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	ips, err := res.LookupNetIP(ctx, "ip4", host)
	if err != nil {
		return netip.Addr{}, err
	}
	for _, ip := range ips {
		ip = ip.Unmap()
		if ip.Is4() {
			return ip, nil
		}
	}
	return netip.Addr{}, fmt.Errorf("no IPv4 address")
}
