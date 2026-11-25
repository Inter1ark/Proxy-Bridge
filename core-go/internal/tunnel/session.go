//go:build darwin || linux

package tunnel

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/netip"
	"os"
	"runtime"
	"sync"
	"syscall"
	"time"

	"golang.zx2c4.com/wireguard/tun"

	"proxybridge/core/internal/engine"
	"proxybridge/core/internal/netcfg"
	"proxybridge/core/internal/rules"
	"proxybridge/core/internal/status"
)

// Config is everything one tunnel session needs.
type Config struct {
	Set *rules.Set
	// Device is a TUN the host already opened (Android: the VpnService file descriptor,
	// iOS: the packet tunnel's utun). When set, the platform owns addresses and routes:
	// nothing is configured here and the proxies are not pinned to a gateway.
	Device tun.Device
	// BindInterface scopes the core's own sockets to this interface (iOS: the physical
	// interface, en0 or pdp_ip0), so they do not loop back into the tunnel. Empty: unbound.
	BindInterface string
	// LowMemory makes the engine keep small buffers (iOS extension memory limit).
	LowMemory  bool
	TunName    string
	TunAddr    netip.Prefix
	Peer       netip.Addr
	MTU        int
	DNS        netip.Addr // invalid: DNS over UDP is dropped
	RouteMode  netcfg.RouteMode
	Exempt     []int
	ParentPID  int
	StatsEvery time.Duration
	UDP        UDPOptions
}

// session is one running tunnel: TUN, routes, engine. It ends when Stop is
// called, when the engine dies or when the parent process disappears.
type Session struct {
	cfg    Config
	log    Logger
	rep    *status.Reporter
	rt     *router
	gw     netcfg.Gateway // the original route (not set when the platform owns the routes)
	gwIdx  int            // index of gw.Interface when the routes on it were last installed
	gwGone bool
	undo   netcfg.Undo
	eng    *engine.Engine
	stats  *engine.Counters
	ifName string

	cancel context.CancelFunc
	done   chan struct{}
	mu     sync.Mutex
	err    error // why the session ended on its own; nil after Stop
}

// BringUp configures the tunnel and reports "up". On failure everything done so
// far is rolled back and the error is reported and returned.
func BringUp(log Logger, rep *status.Reporter, cfg Config) (*Session, error) {
	s := &Session{cfg: cfg, log: log, rep: rep, done: make(chan struct{}), stats: &engine.Counters{}}
	if err := s.setup(); err != nil {
		log.Errorf("%v", err)
		rep.Error(err.Error())
		if uerr := s.undo.Run(); uerr != nil {
			log.Warnf("cleanup finished with errors: %v", uerr)
		}
		rep.Emit(status.Line{Status: "down"})
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	s.cancel = cancel
	go s.loop(ctx)
	return s, nil
}

func (s *Session) setup() error {
	cfg := s.cfg
	external := cfg.Device != nil
	var gw netcfg.Gateway
	if external {
		gw.Interface = cfg.BindInterface
		s.log.Infof("tunnel device supplied by the host: addresses and routes are managed by the platform (own sockets bound to %q)", cfg.BindInterface)
	} else {
		var err error
		gw, err = netcfg.DefaultGateway()
		if err != nil {
			return err
		}
		if gw.Tunnel {
			s.log.Infof("a VPN owns the internet route (interface %s): direct traffic and proxy connections go through it", gw.Interface)
		} else {
			s.log.Infof("original default route: gateway=%s interface=%s", gw.IP, gw.Interface)
		}
	}

	// The router resolves and pins the proxies now, before routing changes.
	udp := cfg.UDP
	if cfg.DNS.IsValid() {
		udp.DNSServer = netip.AddrPortFrom(cfg.DNS, 53)
	}
	var undo *netcfg.Undo
	if !external {
		undo = &s.undo
	}
	var physical netcfg.Gateway
	if !external && gw.Tunnel {
		if p, ok := netcfg.PhysicalGateway(gw.Interface); ok {
			physical = p
			s.log.Infof("network under the VPN: gateway=%s interface=%s (used by the VPN client itself and while the VPN is down)", p.IP, p.Interface)
		}
	}
	if !external {
		s.gw = gw
		s.gwIdx = interfaceIndex(gw.Interface)
	}
	s.rt = newRouter(s.log, gw, physical, undo, cfg.Exempt, udp)
	if err := s.rt.Load(cfg.Set); err != nil {
		return err
	}
	s.undo.Push(func() error { s.rt.Close(); return nil })

	var dev tun.Device
	if external {
		dev = cfg.Device
	} else {
		var err error
		dev, err = tun.CreateTUN(cfg.TunName, cfg.MTU)
		if err != nil {
			return fmt.Errorf("create TUN %q: %v", cfg.TunName, err)
		}
	}
	s.undo.Push(func() error { return dev.Close() })
	ifName, err := dev.Name()
	if err != nil {
		if !external {
			return fmt.Errorf("TUN name: %v", err)
		}
		ifName = "tun"
	}
	s.ifName = ifName
	s.log.Infof("TUN %s %s", ifName, map[bool]string{true: "attached", false: "created"}[external])

	ncfg := netcfg.Config{Interface: ifName, Addr: cfg.TunAddr, Peer: cfg.Peer, MTU: cfg.MTU, Mode: cfg.RouteMode}
	if !external {
		if err := netcfg.ConfigureInterface(ncfg); err != nil {
			return fmt.Errorf("configure %s: %v", ifName, err)
		}
		// Direct connections leave through the original interface (IP_BOUND_IF) and need a
		// default route scoped to it, or the tunnel routes win the lookup.
		if err := netcfg.AddScopedDefault(gw, &s.undo); err != nil {
			s.log.Warnf("scoped default route via %s: %v", gw.Interface, err)
			// Without the scoped route a bound socket cannot reach the direct resolver, so pin
			// it as a last resort. This is not done otherwise: a host route sends EVERY
			// connection of the Mac to that address around the tunnel, and 1.1.1.1 is also a
			// web site (https://1.1.1.1/cdn-cgi/trace is what IP checkers use for their
			// "Cloudflare exit IP"), so pinning it showed the real IP there.
			if cfg.DNS.IsValid() {
				if err := netcfg.AddHostRoute(cfg.DNS, gw, &s.undo); err != nil {
					s.log.Warnf("host route for dns %s: %v", cfg.DNS, err)
				} else {
					s.log.Warnf("dns %s is pinned to %s: connections to that address bypass the tunnel", cfg.DNS, gw.Interface)
				}
			}
		}
	}

	eng, err := engine.New(dev, engine.Options{
		MTU:       uint32(cfg.MTU),
		Dial:      s.rt.directDial,
		Route:     s.rt.Decide,
		RouteUDP:  s.rt.DecideUDP,
		DNS:       dnsHostPort(cfg.DNS),
		Logf:      s.log.Debugf,
		Stats:     s.stats,
		LowMemory: cfg.LowMemory,
	})
	if err != nil {
		return err
	}
	s.eng = eng
	eng.Start()
	s.undo.Push(func() error { eng.Close(); return nil })

	if !external {
		if err := netcfg.RouteAllViaTun(ncfg, gw, &s.undo); err != nil {
			return fmt.Errorf("route traffic via %s: %v", ifName, err)
		}
		if bits := netcfg.SplitBits(); bits > 1 {
			s.log.Infof("tunnel routes installed as /%d: another tunnel holds the shorter prefixes", bits)
		}
		// macOS sends name lookups to the DNS servers of the network service outside the tunnel
		// routes, so with DNS via proxy the services point at the tunnel peer while connected:
		// queries to it enter the engine (a private address is answered through the proxy).
		if udp.DNSViaProxy && runtime.GOOS == "darwin" && cfg.Peer.IsValid() {
			if err := netcfg.SetSystemDNS(cfg.Peer, &s.undo); err != nil {
				s.log.Warnf("system dns: %v (names may be resolved outside the tunnel)", err)
			} else {
				s.log.Infof("system dns -> %s while connected (restored on disconnect)", cfg.Peer)
			}
		}
	}

	s.rep.UpWithRules(ifName, s.rt.Summary(), len(cfg.Set.Rules), s.rt.Procmap())
	s.log.Infof("up: %d rules, all other programs -> %s (process attribution: %s, udp: %s, dns via proxy: %v)",
		len(cfg.Set.Rules), s.rt.Summary(), s.rt.Procmap(), udp.Mode, udp.DNSViaProxy)
	return nil
}

func (s *Session) loop(ctx context.Context) {
	defer close(s.done)
	ticker := time.NewTicker(s.cfg.StatsEvery)
	defer ticker.Stop()
	parentTicker := time.NewTicker(2 * time.Second)
	defer parentTicker.Stop()

	var reason error
loop:
	for {
		select {
		case <-ctx.Done():
			break loop
		case <-s.eng.Done():
			reason = fmt.Errorf("engine stopped unexpectedly")
			break loop
		case <-ticker.C:
			s.rep.Stats(s.stats.Up.Load(), s.stats.Down.Load())
		case <-parentTicker.C:
			s.watchGateway()
			if s.cfg.ParentPID > 0 && !ProcessAlive(s.cfg.ParentPID) {
				s.log.Infof("parent process %d is gone: shutting down the tunnel", s.cfg.ParentPID)
				break loop
			}
		}
	}
	if reason != nil {
		s.log.Errorf("%v", reason)
		s.rep.Error(reason.Error())
	}
	if err := s.undo.Run(); err != nil {
		s.log.Warnf("cleanup finished with errors: %v", err)
	}
	s.mu.Lock()
	s.err = reason
	s.mu.Unlock()
	s.rep.Emit(status.Line{Status: "down"})
	s.log.Infof("tunnel down")
}

// watchGateway notices that the original interface was destroyed and created again (a VPN
// client that reconnected) and puts the routes that lived on it back: the scoped default
// route and the host routes of the proxies. Without them sockets bound to the interface
// have no route and every connection of the session fails until a reconnect.
func (s *Session) watchGateway() {
	if s.gw.Interface == "" {
		return
	}
	idx := interfaceIndex(s.gw.Interface)
	if idx <= 0 {
		if !s.gwGone {
			s.gwGone = true
			s.log.Warnf("interface %s is gone (the VPN disconnected?): waiting for it to come back", s.gw.Interface)
		}
		return
	}
	trouble := s.rt.gwTrouble.Swap(false)
	if idx == s.gwIdx && !s.gwGone && !trouble {
		return
	}
	if idx != s.gwIdx || s.gwGone {
		s.log.Infof("interface %s was created again (index %d -> %d): restoring the routes on it", s.gw.Interface, s.gwIdx, idx)
	}
	s.gwIdx, s.gwGone = idx, false
	if err := netcfg.RepairRoutes(s.gw, s.rt.routedHosts()); err != nil {
		s.log.Warnf("restore routes on %s: %v", s.gw.Interface, err)
	}
}

// interfaceIndex returns the current index of an interface, 0 when it does not exist.
func interfaceIndex(name string) int {
	if name == "" {
		return 0
	}
	iface, err := net.InterfaceByName(name)
	if err != nil {
		return 0
	}
	return iface.Index
}

// Stop tears the session down and waits for the cleanup to finish.
func (s *Session) Stop() {
	s.cancel()
	<-s.done
}

// Done is closed once the session has been torn down.
func (s *Session) Done() <-chan struct{} { return s.done }

// Err tells why the session ended on its own (nil when stopped on request).
func (s *Session) Err() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.err
}

// Reload replaces the rule set of a running session.
func (s *Session) Reload(set *rules.Set) error {
	if err := s.rt.Load(set); err != nil {
		return err
	}
	s.log.Infof("rules replaced: %d rules, %d proxies", len(set.Rules), len(set.Proxies))
	s.rep.Rules(len(set.Rules))
	return nil
}

// startRequest is the JSON a client sends with the "start" command in daemon
// mode: the rule set plus per-session options.
type startRequest struct {
	rules.Set
	DNS         *string `json:"dns,omitempty"`
	ParentPID   int     `json:"parent_pid,omitempty"`
	ExemptPIDs  []int   `json:"exempt_pids,omitempty"`
	UDP         string  `json:"udp,omitempty"`           // direct (default), proxy or block
	DNSViaProxy bool    `json:"dns_via_proxy,omitempty"` // answer udp/53 through the proxy (DNS over TCP)
	DNSProxy    int     `json:"dns_proxy,omitempty"`     // proxy id for DNS queries (0: automatic)
}

// ParseRequest applies a request on top of the daemon defaults.
func ParseRequest(payload []byte, defaults Config) (Config, error) {
	var req startRequest
	if err := json.Unmarshal(payload, &req); err != nil {
		return defaults, fmt.Errorf("start: %w", err)
	}
	set := req.Set
	if err := set.Validate(); err != nil {
		return defaults, err
	}
	cfg := defaults
	cfg.Set = &set
	cfg.UDP = UDPOptions{Mode: req.UDP, DNSViaProxy: req.DNSViaProxy, DNSProxy: req.DNSProxy}
	if err := cfg.UDP.Normalize(); err != nil {
		return defaults, fmt.Errorf("start: %w", err)
	}
	if req.DNSProxy != 0 {
		found := false
		for _, p := range set.Proxies {
			if p.ID == req.DNSProxy {
				found = true
			}
		}
		if !found {
			cfg.UDP.DNSProxy = 0 // unknown id: automatic choice
		}
	}
	cfg.ParentPID = req.ParentPID
	cfg.Exempt = append([]int{os.Getpid()}, req.ExemptPIDs...)
	if req.ParentPID > 0 {
		cfg.Exempt = append(cfg.Exempt, req.ParentPID)
	}
	if req.DNS != nil {
		cfg.DNS = netip.Addr{}
		if *req.DNS != "" {
			a, err := netip.ParseAddr(*req.DNS)
			if err != nil || !a.Is4() {
				return defaults, fmt.Errorf("start: invalid dns %q", *req.DNS)
			}
			cfg.DNS = a
		}
	}
	return cfg, nil
}

func dnsHostPort(a netip.Addr) string {
	if !a.IsValid() {
		return ""
	}
	return net.JoinHostPort(a.String(), "53")
}

// ProcessAlive reports whether a process with this pid exists (EPERM counts as alive).
func ProcessAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}
