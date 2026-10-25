//go:build darwin || linux

package tunnel

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/netip"
	"os"
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
	s.rt = newRouter(s.log, gw, undo, cfg.Exempt, udp)
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
		}
		// The direct resolver must stay reachable outside the tunnel.
		if cfg.DNS.IsValid() {
			if err := netcfg.AddHostRoute(cfg.DNS, gw, &s.undo); err != nil {
				// Not fatal: the resolver may be on the local link already, or be a proxy IP.
				s.log.Warnf("host route for dns %s: %v", cfg.DNS, err)
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
