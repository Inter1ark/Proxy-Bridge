//go:build darwin || linux

package tunnel

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
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
	log     Logger
	procs   *procmap.Resolver
	direct  *net.Dialer
	base    *net.Dialer
	gateway netcfg.Gateway
	undo    *netcfg.Undo
	exempt  map[int]bool
	udp     UDPOptions

	mu      sync.Mutex
	routed  map[netip.Addr]bool // proxy IPs that already have a host route
	current atomic.Pointer[ruleTable]

	// unknownLogged rate-limits the "owner unknown" log line.
	unknownLogged    atomic.Int64
	unknownUDPLogged atomic.Int64

	// physical is set when the original gateway is a VPN and the network card under it has
	// its own default route: the VPN client's own connections leave through it, and so do
	// direct connections while the VPN interface is gone (see dialOut).
	physical     *net.Dialer
	physicalName string
	// gwTrouble is raised when a socket bound to the original interface could not be used;
	// the session then puts the routes on that interface back (see Session.watchGateway).
	gwTrouble   atomic.Bool
	// vpnNets is set when the VPN under the session splits the traffic itself (its own list of
	// networks instead of the whole internet): direct traffic to those networks goes through
	// the VPN, everything else through the network card, as it would without ProxyBridge.
	vpnNets *netcfg.PrefixSet
	// sniffNames makes proxied flows on web ports send the site name to the proxy (set when
	// the system DNS is left to a VPN, so names are still resolved on the proxy's side).
	sniffNames bool
	vpnDownNote atomic.Int64
	// vpnOK and vpnFail are the times (unix ms) of the last TCP connection through the
	// original interface that succeeded, and of the last one that could not get through it.
	vpnOK   atomic.Int64
	vpnFail atomic.Int64
}

// ruleTable is an immutable, resolved rule set.
type ruleTable struct {
	// noted holds the program+decision pairs already written to the log (see noteOnce).
	noted      sync.Map
	notedCount atomic.Int32

	set     *rules.Set
	dialers map[int]*proxy.Dialer
	udp     map[int]*udpPool       // SOCKS5 UDP associations per proxy id
	dns     map[int]*proxyResolver // DNS through each proxy (DoH on 443, else TCP/53)
	// fallback answers lookups from the regular DNS while a proxy is unreachable. It is
	// filled only when "all other programs" go direct (see dnsFallback).
	fallback map[int]*dnsFallback
	// summary is shown in the "up" status line: the catch-all proxy or "direct".
	summary string
}

func newRouter(log Logger, gw netcfg.Gateway, physical netcfg.Gateway, undo *netcfg.Undo, exempt []int, udp UDPOptions) *router {
	base := boundDialer(gw.Interface)
	r := &router{
		log:     log,
		direct:  base,
		base:    base,
		gateway: gw,
		undo:    undo,
		exempt:  make(map[int]bool, len(exempt)),
		routed:  make(map[netip.Addr]bool),
		udp:     udp,
	}
	for _, pid := range exempt {
		if pid > 0 {
			r.exempt[pid] = true
		}
	}
	if physical.Interface != "" && physical.Interface != gw.Interface {
		r.physical = boundDialer(physical.Interface)
		r.physicalName = physical.Interface
	}
	r.procs = procmap.New(log.Infof)
	return r
}

// routedHosts lists the proxy addresses that have a host route on the original interface.
func (r *router) routedHosts() []netip.Addr {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]netip.Addr, 0, len(r.routed))
	for ip := range r.routed {
		out = append(out, ip)
	}
	return out
}

// interfaceUnusable tells whether a dial failed because the interface the socket is bound
// to is gone or has no address and no route: a VPN that is down or reconnecting.
func interfaceUnusable(err error) bool {
	for _, e := range []error{syscall.EADDRNOTAVAIL, syscall.ENETUNREACH, syscall.EHOSTUNREACH, syscall.ENETDOWN, syscall.ENXIO} {
		if errors.Is(err, e) {
			return true
		}
	}
	return false
}

// vpnClientMarks are parts of the executable path of programs that build tunnels
// themselves. Their own connections leave through the network card first: what such a
// program sends is, above all, its transport to the VPN server, and transport sent into
// the VPN's own interface is a loop that takes the whole VPN down without a single error
// (the interface accepts every connection at once and then has nowhere to send it). Only
// when the network card does not let the connection out (the client's own kill switch) is
// it sent through the VPN.
// The marks are narrow on purpose: a program taken for a VPN client by mistake would send
// its traffic around the VPN.
var vpnClientMarks = []string{
	"packettunnel", "tunnelprovider", "networkextension", "/neagent", "nesessionmanager",
	"/library/systemextensions/", "wireguard", "openvpn", "amnezia", "sing-box", "/xray", "/v2ray",
	"v2box.app/", "happ.app/", "hiddify", "streisand.app/", "clashx", "clash verge",
	"shadowrocket", "nekoray", "tailscale", "vpn",
}

func isVPNClient(exe string) bool {
	if exe == "" {
		return false
	}
	low := strings.ToLower(exe)
	for _, m := range vpnClientMarks {
		if strings.Contains(low, m) {
			return true
		}
	}
	return false
}

// dialOut opens a connection outside the tunnel through the original interface. When that
// interface is a VPN that cannot be used right now, the network card under it is used
// instead: without ProxyBridge the system would do the same.
func (r *router) dialOut(ctx context.Context, network, target string, timeout time.Duration) (net.Conn, error) {
	if r.vpnNets != nil && r.physical != nil {
		if ap, err := netip.ParseAddrPort(target); err == nil && ap.Addr().Unmap().Is4() && !r.vpnNets.Contains(ap.Addr().Unmap()) && !isPrivate(ap.Addr().Unmap()) {
			return r.physicalDialNet(ctx, network, target, timeout)
		}
	}
	d := *r.direct
	d.Timeout = timeout
	c, err := d.DialContext(ctx, network, target)
	if network == "tcp" {
		r.noteVPN(err)
	}
	if err == nil || !interfaceUnusable(err) {
		return c, err
	}
	r.gwTrouble.Store(true)
	if r.physical == nil {
		return nil, err
	}
	now := time.Now().Unix()
	if last := r.vpnDownNote.Load(); now-last >= 30 && r.vpnDownNote.CompareAndSwap(last, now) {
		r.log.Warnf("the VPN interface %s cannot be used (%v): direct connections leave through %s until it is back", r.gateway.Interface, err, r.physicalName)
	}
	return r.physicalDialNet(ctx, network, target, timeout)
}

func (r *router) physicalDialNet(ctx context.Context, network, target string, timeout time.Duration) (net.Conn, error) {
	p := *r.physical
	p.Timeout = timeout
	return p.DialContext(ctx, network, target)
}

// noteVPN records whether a TCP connection got through the original interface. A refused
// connection or a reset is an answer from the other side and counts as "got through".
func (r *router) noteVPN(err error) {
	now := time.Now().UnixMilli()
	var ne net.Error
	if err == nil || !(interfaceUnusable(err) || (errors.As(err, &ne) && ne.Timeout()) || errors.Is(err, context.DeadlineExceeded)) {
		r.vpnOK.Store(now)
		return
	}
	r.vpnFail.Store(now)
}

// vpnDownAfter is how long without a connection through the VPN, after a failed one, makes
// the VPN count as down.
const vpnDownAfter = 8 * time.Second

// vpnDown tells whether the VPN under the session looks dead: its interface is gone, or
// connections through it keep failing.
func (r *router) vpnDown() bool {
	if interfaceIndex(r.gateway.Interface) <= 0 {
		return true
	}
	ok, fail := r.vpnOK.Load(), r.vpnFail.Load()
	return fail > ok && time.Now().UnixMilli()-ok > vpnDownAfter.Milliseconds()
}

// vpnClientProbe is how long a VPN client's connection may take through the network card
// before it is tried through the VPN instead.
const vpnClientProbe = 3 * time.Second

// vpnClientDial is the dial function for the connections of a VPN client: through the
// network card, and through the VPN only when the card does not let the connection out.
func (r *router) vpnClientDial(ctx context.Context, target string) (net.Conn, error) {
	c, err := r.physicalDialNet(ctx, "tcp", target, vpnClientProbe)
	if err == nil {
		return c, nil
	}
	if ctx.Err() != nil {
		return nil, err
	}
	return r.dialOut(ctx, "tcp", target, 15*time.Second)
}

// Procmap names the process attribution method for the status line.
func (r *router) Procmap() string { return r.procs.Method() }

// Load resolves the proxies of a set, pins their IPs to the real gateway and
// makes the set current. Flows already open are not affected.
func (r *router) Load(set *rules.Set) error {
	if err := set.Validate(); err != nil {
		return err
	}
	t := &ruleTable{set: set, dialers: make(map[int]*proxy.Dialer, len(set.Proxies)), udp: make(map[int]*udpPool), dns: make(map[int]*proxyResolver), fallback: make(map[int]*dnsFallback), summary: "direct"}
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
		t.udp[p.ID] = newUDPPool(p.ID, t.dialers[p.ID], r.log)
		t.dns[p.ID] = newProxyResolver(p.ID, t.dialers[p.ID], r.log)
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
	if t.summary == "direct" && r.udp.DNSViaProxy {
		for id := range t.dns {
			t.fallback[id] = newDNSFallback(id, r.log, r.askRegularDNS)
		}
	}
	if old := r.current.Swap(t); old != nil {
		for _, pool := range old.udp {
			pool.closeAll()
		}
		for _, res := range old.dns {
			res.Close()
		}
	}
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

// Close drops the UDP associations of the current table.
func (r *router) Close() {
	if t := r.current.Load(); t != nil {
		for _, pool := range t.udp {
			pool.closeAll()
		}
		for _, res := range t.dns {
			res.Close()
		}
	}
}

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
	rule, ok, via := matchTree(t.set, pid, exe, r.parentOf)
	if r.physical != nil && (!ok || isCatchAll(rule)) && isVPNClient(exe) {
		r.log.Debugf("tcp %s -> %s: pid %d %q: a VPN client: outside the VPN through %s", src, dst, pid, exe, r.physicalName)
		r.noteOnce(t, exe, pid, "a VPN client: its own connections leave through "+r.physicalName+" (to "+dst.String()+")")
		return engine.Decision{Dial: r.vpnClientDial, Label: "direct"}
	}
	if !ok {
		r.log.Debugf("tcp %s -> %s: pid %d %q: no rule: direct", src, dst, pid, exe)
		r.noteOnce(t, exe, pid, "no rule: direct")
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	}
	switch rule.Action {
	case rules.ActionBlock:
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q%s: block", src, dst, pid, exe, rule.Process, via)
		r.noteOnce(t, exe, pid, fmt.Sprintf("rule %q%s: block", rule.Process, via))
		return engine.Decision{Block: true, Label: "block"}
	case rules.ActionDirect:
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q%s: direct", src, dst, pid, exe, rule.Process, via)
		r.noteOnce(t, exe, pid, fmt.Sprintf("rule %q%s: direct", rule.Process, via))
		return engine.Decision{Dial: r.directDial, Label: "direct"}
	default:
		d := t.dialers[rule.Proxy]
		r.log.Debugf("tcp %s -> %s: pid %d %q: rule %q%s: proxy %d", src, dst, pid, exe, rule.Process, via, rule.Proxy)
		r.noteOnce(t, exe, pid, fmt.Sprintf("rule %q%s: proxy %d", rule.Process, via, rule.Proxy))
		// An IPv6 connection goes to the proxy by site name whenever the name can be read:
		// many proxies have no IPv6 exit, and by name the proxy picks the address it can reach.
		return engine.Decision{Sniff: r.sniffNames || dst.Addr().Unmap().Is6(), Dial: func(ctx context.Context, target string) (net.Conn, error) {
			c, err := d.Dial(ctx, target)
			if err != nil && interfaceUnusable(err) {
				// the socket to the proxy is bound to the original interface too
				r.gwTrouble.Store(true)
			}
			return c, err
		}, Label: "proxy " + strconv.Itoa(rule.Proxy)}
	}
}

// noteLimit bounds the "first decision" lines of one rule table.
const noteLimit = 300

// noteOnce writes one info line per program and decision for the current rule table, so the
// support log shows which rule every program fell under without a line per connection.
func (r *router) noteOnce(t *ruleTable, exe string, pid int, decision string) {
	if exe == "" {
		return
	}
	if _, seen := t.noted.LoadOrStore(exe+"|"+decision, struct{}{}); seen {
		return
	}
	if t.notedCount.Add(1) > noteLimit {
		return
	}
	r.log.Infof("program %q (pid %d): %s", exe, pid, decision)
}

// inheritDepth bounds the walk up the process tree.
const inheritDepth = 8

// matchTree finds the rule for a process. Its own specific rule wins; without one, the
// nearest ancestor's specific rule applies (an IDE's terminal, an AI agent's tool
// processes and a launcher's real binary follow the program they belong to); the
// catch-all "*" applies only when no ancestor has a rule. parent answers (ppid, exe, ok);
// via describes an inherited match for the log.
func matchTree(set *rules.Set, pid int, exe string, parent func(int) (int, string, bool)) (rule rules.Rule, ok bool, via string) {
	rule, ok = set.Match(exe)
	if ok && !isCatchAll(rule) {
		return rule, true, ""
	}
	cur := pid
	for depth := 0; depth < inheritDepth && cur > 1; depth++ {
		ppid, pexe, pok := parent(cur)
		if !pok {
			break
		}
		if r, m := set.Match(pexe); m && !isCatchAll(r) {
			return r, true, " inherited from " + strconv.Quote(pexe) + " (pid " + strconv.Itoa(ppid) + ")"
		}
		cur = ppid
	}
	return rule, ok, ""
}

func isCatchAll(r rules.Rule) bool { return strings.TrimSpace(r.Process) == "*" }

func (r *router) parentOf(pid int) (int, string, bool) {
	ppid, ok := r.procs.Parent(pid)
	if !ok {
		return 0, "", false
	}
	exe, _ := r.procs.Exe(ppid)
	return ppid, exe, true
}

// regularResolvers lists the DNS servers outside the tunnel: the configured direct
// resolver and the router of the local network (home routers answer DNS themselves, and an
// internet provider that drops foreign resolvers still serves its own).
func (r *router) regularResolvers() []netip.AddrPort {
	var out []netip.AddrPort
	if r.udp.DNSServer.IsValid() {
		out = append(out, r.udp.DNSServer)
	}
	if ip := r.gateway.IP; ip.IsValid() && ip.Is4() && isPrivate(ip) {
		out = append(out, netip.AddrPortFrom(ip, 53))
	}
	return out
}

// askRegularDNS answers a query from the resolvers outside the tunnel.
func (r *router) askRegularDNS(ctx context.Context, query []byte) ([]byte, error) {
	return askResolvers(ctx, r.regularResolvers(), query, func(ctx context.Context, addr string) (udpConn, error) {
		return r.dialOut(ctx, "udp", addr, 3*time.Second)
	})
}

// ping forwards an ICMP echo request outside the tunnel: through the original interface,
// or through the network card while a VPN under the session cannot be used. Ping has no
// ports, so the program that sent it is unknown; it is forwarded only while programs
// without a rule go direct, and never when everything goes through a proxy.
func (r *router) ping(dst netip.Addr, msg []byte) ([]byte, error) {
	if t := r.current.Load(); t == nil || t.summary != "direct" {
		return nil, errors.New("ping is not forwarded while all programs go through a proxy")
	}
	if r.gateway.Interface == "" {
		return nil, errors.New("ping: no original interface")
	}
	if r.vpnNets != nil && r.physical != nil && !r.vpnNets.Contains(dst) && !isPrivate(dst) {
		return pingThrough(r.physicalName, dst, msg)
	}
	reply, err := pingThrough(r.gateway.Interface, dst, msg)
	if err != nil && r.physical != nil && interfaceUnusable(err) {
		return pingThrough(r.physicalName, dst, msg)
	}
	return reply, err
}

// directDial opens the destination outside the tunnel.
func (r *router) directDial(ctx context.Context, target string) (net.Conn, error) {
	return r.dialOut(ctx, "tcp", target, 15*time.Second)
}

func (r *router) pin(ip netip.Addr) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.routed[ip] || r.undo == nil {
		return nil // routes are managed by the platform (Android VpnService)
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
