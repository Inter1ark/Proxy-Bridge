//go:build darwin || linux

package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strconv"
	"sync"
	"time"

	"proxybridge/core/internal/engine"
	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/rules"
)

// UDP policy for programs whose rule says proxy (the GUI setting "UDP and WebRTC").
const (
	udpDirect = "direct" // leave UDP alone: WebRTC and QUIC reveal the real IP
	udpProxy  = "proxy"  // via SOCKS5 UDP ASSOCIATE; blocked when the proxy refuses UDP
	udpBlock  = "block"  // always dropped: no leaks, applications fall back to TCP
)

// udpOptions is the per-session UDP and DNS policy.
type udpOptions struct {
	Mode        string
	DNSViaProxy bool
	DNSProxy    int            // proxy id for DNS queries (0: automatic)
	DNSServer   netip.AddrPort // replaces private resolvers when DNS goes via proxy; also the direct resolver
}

func (o *udpOptions) normalize() error {
	switch o.Mode {
	case "", udpDirect:
		o.Mode = udpDirect
	case udpProxy, udpBlock:
	default:
		return fmt.Errorf("udp mode %q: want direct, proxy or block", o.Mode)
	}
	return nil
}

// DecideUDP implements engine.UDPRouteFunc.
func (r *router) DecideUDP(src, dst netip.AddrPort) engine.UDPDecision {
	t := r.current.Load()
	isDNS := dst.Port() == 53
	if t == nil {
		if isDNS {
			return r.directDNS(dst)
		}
		return r.directUDP(dst)
	}
	pid, exe, known := r.procs.LookupUDP(src, dst)
	if known && r.exempt[pid] {
		if isDNS {
			return r.directDNS(dst)
		}
		return r.directUDP(dst)
	}
	rule, matched := t.set.Match(exe)

	if isDNS {
		if !r.udp.DNSViaProxy {
			return r.directDNS(dst)
		}
		// Names are resolved by mDNSResponder, not by the application, so every
		// query goes through the DNS proxy (the matching rule's proxy when there is one).
		id := 0
		if matched && rule.Action == rules.ActionProxy {
			id = rule.Proxy
		} else if matched && rule.Action == rules.ActionBlock {
			r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: block", src, dst, pid, exe, rule.Process)
			return engine.UDPDecision{Block: true, Label: "block"}
		}
		if id == 0 {
			id = t.dnsProxyChoice(r.udp.DNSProxy)
		}
		d := t.dialers[id]
		if d == nil {
			r.log.Debugf("udp %s -> %s: dns via proxy: no proxy registered: direct", src, dst)
			return r.directDNS(dst)
		}
		resolver := dst
		if isPrivate(dst.Addr()) {
			resolver = r.udp.DNSServer
		}
		r.log.Debugf("udp %s -> %s: pid %d %q: dns via proxy %d -> %s", src, dst, pid, exe, id, resolver)
		return engine.UDPDecision{Label: "dns via proxy " + strconv.Itoa(id), Open: func(ctx context.Context) (engine.UDPSession, error) {
			return newDNSSession(d, resolver), nil
		}}
	}

	if !matched {
		r.log.Debugf("udp %s -> %s: pid %d %q: no rule: direct", src, dst, pid, exe)
		return r.directUDP(dst)
	}
	switch rule.Action {
	case rules.ActionBlock:
		r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: block", src, dst, pid, exe, rule.Process)
		return engine.UDPDecision{Block: true, Label: "block"}
	case rules.ActionDirect:
		r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: direct", src, dst, pid, exe, rule.Process)
		return r.directUDP(dst)
	}
	switch r.udp.Mode {
	case udpBlock:
		r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: blocked (udp mode block)", src, dst, pid, exe, rule.Process)
		return engine.UDPDecision{Block: true, Label: "block"}
	case udpDirect:
		r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: direct (udp mode direct)", src, dst, pid, exe, rule.Process)
		return r.directUDP(dst)
	}
	pool := t.udp[rule.Proxy]
	if pool == nil || pool.refused() {
		r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: proxy %d has no UDP: blocked", src, dst, pid, exe, rule.Process, rule.Proxy)
		return engine.UDPDecision{Block: true, Label: "block: proxy has no udp"}
	}
	r.log.Debugf("udp %s -> %s: pid %d %q: rule %q: proxy %d", src, dst, pid, exe, rule.Process, rule.Proxy)
	return engine.UDPDecision{Label: "proxy " + strconv.Itoa(rule.Proxy), Open: func(ctx context.Context) (engine.UDPSession, error) {
		return pool.open(ctx, src, dst)
	}}
}

// directUDP opens a connected socket to dst outside the tunnel.
func (r *router) directUDP(dst netip.AddrPort) engine.UDPDecision {
	return engine.UDPDecision{Label: "direct", Open: func(ctx context.Context) (engine.UDPSession, error) {
		d := *r.direct
		c, err := d.DialContext(ctx, "udp", dst.String())
		if err != nil {
			return nil, err
		}
		return engine.NewConnSession(c, 60*time.Second), nil
	}}
}

// directDNS forwards a DNS query to the configured direct resolver (or, when
// none is configured, drops DNS over UDP as before).
func (r *router) directDNS(dst netip.AddrPort) engine.UDPDecision {
	if !r.udp.DNSServer.IsValid() {
		return engine.UDPDecision{Block: true, Label: "dns dropped"}
	}
	resolver := r.udp.DNSServer
	return engine.UDPDecision{Label: "dns direct", Open: func(ctx context.Context) (engine.UDPSession, error) {
		d := *r.direct
		c, err := d.DialContext(ctx, "udp", resolver.String())
		if err != nil {
			return nil, err
		}
		return engine.NewConnSession(c, 10*time.Second), nil
	}}
}

// dnsProxyChoice picks the proxy for DNS queries: the explicit one, else the
// catch-all proxy, else the first PROXY rule's proxy, else the first proxy.
func (t *ruleTable) dnsProxyChoice(explicit int) int {
	if explicit != 0 && t.dialers[explicit] != nil {
		return explicit
	}
	for _, rule := range t.set.Rules {
		if rule.Process == "*" && rule.Action == rules.ActionProxy {
			return rule.Proxy
		}
	}
	for _, rule := range t.set.Rules {
		if rule.Action == rules.ActionProxy {
			return rule.Proxy
		}
	}
	for _, p := range t.set.Proxies {
		return p.ID
	}
	return 0
}

func isPrivate(a netip.Addr) bool {
	return a.IsPrivate() || a.IsLoopback() || a.IsLinkLocalUnicast() || (a.Is4() && a.As4()[0] == 100 && a.As4()[1]&0xC0 == 64)
}

// ---------------------------------------------------------------------------
// DNS over TCP through a proxy: one session per client flow, one TCP
// connection per query (resolvers close idle TCP sessions quickly anyway).
// ---------------------------------------------------------------------------

type dnsSession struct {
	dialer   *proxy.Dialer
	resolver netip.AddrPort
	answers  chan []byte
	done     chan struct{}
	once     sync.Once
}

func newDNSSession(d *proxy.Dialer, resolver netip.AddrPort) *dnsSession {
	return &dnsSession{dialer: d, resolver: resolver, answers: make(chan []byte, 16), done: make(chan struct{})}
}

func (s *dnsSession) Write(query []byte) error {
	if len(query) == 0 || len(query) > 65535 {
		return errors.New("dns: bad query size")
	}
	q := make([]byte, len(query))
	copy(q, query)
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		answer, err := dnsOverTCP(ctx, s.dialer, s.resolver, q)
		if err != nil {
			return
		}
		select {
		case s.answers <- answer:
		case <-s.done:
		default:
		}
	}()
	return nil
}

func (s *dnsSession) Read(buf []byte) (int, error) {
	select {
	case a := <-s.answers:
		return copy(buf, a), nil
	case <-s.done:
		return 0, io.EOF
	case <-time.After(10 * time.Second):
		return 0, errors.New("dns: idle")
	}
}

func (s *dnsSession) Close() error {
	s.once.Do(func() { close(s.done) })
	return nil
}

// dnsOverTCP sends one query (length-prefixed) to resolver:53 through the proxy.
func dnsOverTCP(ctx context.Context, d *proxy.Dialer, resolver netip.AddrPort, query []byte) ([]byte, error) {
	c, err := d.Dial(ctx, resolver.String())
	if err != nil {
		return nil, err
	}
	defer c.Close()
	_ = c.SetDeadline(time.Now().Add(10 * time.Second))
	frame := append([]byte{byte(len(query) >> 8), byte(len(query))}, query...)
	if _, err := c.Write(frame); err != nil {
		return nil, err
	}
	var l [2]byte
	if _, err := io.ReadFull(c, l[:]); err != nil {
		return nil, err
	}
	n := int(l[0])<<8 | int(l[1])
	if n == 0 || n > 65535 {
		return nil, errors.New("dns: bad answer length")
	}
	answer := make([]byte, n)
	if _, err := io.ReadFull(c, answer); err != nil {
		return nil, err
	}
	return answer, nil
}

// ---------------------------------------------------------------------------
// SOCKS5 UDP: one association per client socket (source address), shared by
// all destinations that socket talks to; replies are demultiplexed by source.
// ---------------------------------------------------------------------------

type udpPool struct {
	dialer *proxy.Dialer
	log    logger
	id     int

	mu       sync.Mutex
	assocs   map[netip.AddrPort]*udpAssoc
	noUDPTil time.Time
}

func newUDPPool(id int, d *proxy.Dialer, log logger) *udpPool {
	return &udpPool{dialer: d, log: log, id: id, assocs: make(map[netip.AddrPort]*udpAssoc)}
}

// refused reports that the proxy recently refused UDP ASSOCIATE.
func (p *udpPool) refused() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return time.Now().Before(p.noUDPTil)
}

func (p *udpPool) open(ctx context.Context, src, dst netip.AddrPort) (engine.UDPSession, error) {
	p.mu.Lock()
	a := p.assocs[src]
	if a != nil && a.isClosed() {
		delete(p.assocs, src)
		a = nil
	}
	p.mu.Unlock()
	if a == nil {
		ua, err := p.dialer.UDPAssociate(ctx)
		if err != nil {
			if errors.Is(err, proxy.ErrNoUDP) {
				p.mu.Lock()
				p.noUDPTil = time.Now().Add(60 * time.Second)
				p.mu.Unlock()
				p.log.Infof("proxy %d refuses UDP ASSOCIATE: its UDP is blocked for 60 s (no leak)", p.id)
			}
			return nil, err
		}
		a = &udpAssoc{a: ua, flows: make(map[netip.AddrPort]chan []byte), pool: p, src: src}
		p.mu.Lock()
		if existing := p.assocs[src]; existing != nil && !existing.isClosed() {
			p.mu.Unlock()
			ua.Close()
			a = existing
		} else {
			p.assocs[src] = a
			p.mu.Unlock()
			go a.readLoop()
		}
	}
	ch := a.register(dst)
	if ch == nil {
		return nil, errors.New("udp association closed")
	}
	return &udpFlow{assoc: a, dst: dst, ch: ch}, nil
}

func (p *udpPool) forget(a *udpAssoc) {
	p.mu.Lock()
	if p.assocs[a.src] == a {
		delete(p.assocs, a.src)
	}
	p.mu.Unlock()
}

// closeAll drops every association (rule set replaced or session ending).
func (p *udpPool) closeAll() {
	p.mu.Lock()
	assocs := p.assocs
	p.assocs = make(map[netip.AddrPort]*udpAssoc)
	p.mu.Unlock()
	for _, a := range assocs {
		a.close()
	}
}

type udpAssoc struct {
	a    *proxy.UDPAssoc
	pool *udpPool
	src  netip.AddrPort

	mu     sync.Mutex
	flows  map[netip.AddrPort]chan []byte
	closed bool
}

func (a *udpAssoc) isClosed() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.closed
}

func (a *udpAssoc) register(dst netip.AddrPort) chan []byte {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.closed {
		return nil
	}
	if ch, ok := a.flows[dst]; ok {
		return ch
	}
	ch := make(chan []byte, 256)
	a.flows[dst] = ch
	return ch
}

func (a *udpAssoc) unregister(dst netip.AddrPort) {
	a.mu.Lock()
	delete(a.flows, dst)
	empty := len(a.flows) == 0
	a.mu.Unlock()
	if empty {
		a.close()
	}
}

func (a *udpAssoc) close() {
	a.mu.Lock()
	if a.closed {
		a.mu.Unlock()
		return
	}
	a.closed = true
	flows := a.flows
	a.flows = make(map[netip.AddrPort]chan []byte)
	a.mu.Unlock()
	a.a.Close()
	for _, ch := range flows {
		close(ch)
	}
	a.pool.forget(a)
}

func (a *udpAssoc) readLoop() {
	buf := make([]byte, 65535)
	for {
		n, from, err := a.a.ReadFrom(buf, time.Now().Add(90*time.Second))
		if err != nil {
			a.close()
			return
		}
		a.mu.Lock()
		ch := a.flows[from]
		if ch == nil && from.Addr().IsUnspecified() && len(a.flows) == 1 {
			for _, c := range a.flows { // domain-addressed reply: only one flow, it must be the one
				ch = c
			}
		}
		a.mu.Unlock()
		if ch == nil {
			continue
		}
		pkt := make([]byte, n)
		copy(pkt, buf[:n])
		select {
		case ch <- pkt:
		default: // slow consumer: drop, UDP is lossy by nature
		}
	}
}

type udpFlow struct {
	assoc *udpAssoc
	dst   netip.AddrPort
	ch    chan []byte
	once  sync.Once
}

func (f *udpFlow) Write(p []byte) error { return f.assoc.a.WriteTo(p, f.dst) }

func (f *udpFlow) Read(buf []byte) (int, error) {
	select {
	case pkt, ok := <-f.ch:
		if !ok {
			return 0, io.EOF
		}
		return copy(buf, pkt), nil
	case <-time.After(60 * time.Second):
		return 0, errors.New("udp: idle")
	}
}

func (f *udpFlow) Close() error {
	f.once.Do(func() { f.assoc.unregister(f.dst) })
	return nil
}

// net.Dialer is used for direct UDP through the bound dialer; keep the import honest.
var _ = net.Dialer{}
