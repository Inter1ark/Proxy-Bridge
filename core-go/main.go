//go:build darwin || linux

// pbcore routes the TCP traffic of the machine through SOCKS5 or HTTP proxies
// using a TUN interface and a userspace network stack. Which program's
// connections go where is decided by per-program rules (see internal/rules).
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"flag"
	"fmt"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"golang.zx2c4.com/wireguard/tun"

	"proxybridge/core/internal/control"
	"proxybridge/core/internal/engine"
	"proxybridge/core/internal/netcfg"
	"proxybridge/core/internal/rules"
	"proxybridge/core/internal/status"
)

// version is set at build time: -ldflags "-X main.version=X.Y.Z"
var version = "dev"

type logger struct {
	level int
}

const (
	levelDebug = iota
	levelInfo
	levelWarn
	levelError
)

func parseLevel(s string) int {
	switch strings.ToLower(s) {
	case "debug":
		return levelDebug
	case "warn", "warning":
		return levelWarn
	case "error":
		return levelError
	default:
		return levelInfo
	}
}

func (l logger) logf(level int, format string, args ...any) {
	if level < l.level {
		return
	}
	fmt.Fprintf(os.Stderr, "%s %s\n", time.Now().Format("15:04:05.000"), fmt.Sprintf(format, args...))
}

func (l logger) Debugf(f string, a ...any) { l.logf(levelDebug, f, a...) }
func (l logger) Infof(f string, a ...any)  { l.logf(levelInfo, f, a...) }
func (l logger) Warnf(f string, a ...any)  { l.logf(levelWarn, f, a...) }
func (l logger) Errorf(f string, a ...any) { l.logf(levelError, f, a...) }

func main() {
	var (
		proxyURL     = flag.String("proxy", "", "route everything through this proxy: socks5://user:pass@host:port or http://user:pass@host:port")
		proxyFile    = flag.String("proxy-file", "", "read the proxy URL from this file instead of --proxy; the file is deleted after reading")
		rulesFile    = flag.String("rules-file", "", "JSON rule set (proxies and per-program rules); the file is deleted after reading")
		exemptPIDs   = flag.String("exempt-pid", "", "comma separated pids whose connections always go direct (the parent pid is exempt by default)")
		tunName      = flag.String("tun-name", defaultTunName, "TUN interface name (darwin: utun or utunN, auto-assigned)")
		tunAddr      = flag.String("tun-addr", "10.255.0.2/24", "TUN local address with prefix")
		tunPeer      = flag.String("tun-peer", "", "TUN point-to-point peer address (default: first host of --tun-addr)")
		mtu          = flag.Int("mtu", 1500, "TUN MTU")
		dns          = flag.String("dns", "1.1.1.1", "resolver that udp/53 is forwarded to directly (empty: drop DNS over UDP)")
		dnsAlias     = flag.String("dns-direct", "", "alias of --dns")
		routeMode    = flag.String("route-mode", string(netcfg.RouteSplit), "split (0/1 + 128/1 routes, default route untouched) or replace (default route swapped and restored on exit)")
		controlPort  = flag.Int("control-port", 34050, "localhost control socket port (0 disables)")
		controlToken = flag.String("control-token", "", "token required by the control socket (random if empty)")
		parentPID    = flag.Int("parent-pid", 0, "exit when this process disappears (0 disables)")
		statsEvery   = flag.Duration("stats-interval", 2*time.Second, "how often stats lines are printed")
		logLevel     = flag.String("log-level", "info", "debug, info, warn or error (stderr)")
		showVersion  = flag.Bool("version", false, "print version and exit")
	)
	flag.Parse()

	if *showVersion {
		fmt.Println("pbcore", version)
		return
	}

	log := logger{level: parseLevel(*logLevel)}
	rep := status.New()
	fail := func(format string, args ...any) {
		msg := fmt.Sprintf(format, args...)
		log.Errorf("%s", msg)
		rep.Error(msg)
	}

	if *dnsAlias != "" {
		*dns = *dnsAlias
	}

	if os.Geteuid() != 0 {
		fail("pbcore must run as root (use sudo)")
		os.Exit(1)
	}

	set, err := loadRules(*rulesFile, *proxyFile, *proxyURL)
	if err != nil {
		fail("%v", err)
		os.Exit(1)
	}

	localPrefix, err := netip.ParsePrefix(*tunAddr)
	if err != nil || !localPrefix.Addr().Is4() {
		fail("invalid --tun-addr %q (expected IPv4 CIDR)", *tunAddr)
		os.Exit(1)
	}
	var peer netip.Addr
	if *tunPeer != "" {
		peer, err = netip.ParseAddr(*tunPeer)
		if err != nil || !peer.Is4() {
			fail("invalid --tun-peer %q", *tunPeer)
			os.Exit(1)
		}
	} else {
		peer = localPrefix.Masked().Addr().Next()
		if peer == localPrefix.Addr() {
			peer = peer.Next()
		}
	}

	var dnsAddr netip.Addr
	if *dns != "" {
		dnsAddr, err = netip.ParseAddr(*dns)
		if err != nil || !dnsAddr.Is4() {
			fail("invalid --dns %q (expected an IPv4 address)", *dns)
			os.Exit(1)
		}
	}

	token := *controlToken
	if token == "" {
		b := make([]byte, 16)
		if _, err := rand.Read(b); err != nil {
			fail("random token: %v", err)
			os.Exit(1)
		}
		token = hex.EncodeToString(b)
		log.Infof("control token: %s", token)
	}

	exempt := []int{os.Getpid(), *parentPID}
	for _, part := range strings.Split(*exemptPIDs, ",") {
		if pid, err := strconv.Atoi(strings.TrimSpace(part)); err == nil && pid > 0 {
			exempt = append(exempt, pid)
		}
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	netcfg.Logf = log.Debugf
	var undo netcfg.Undo
	exitCode := 0
	defer func() {
		if err := undo.Run(); err != nil {
			log.Warnf("cleanup finished with errors: %v", err)
		}
		rep.Emit(status.Line{Status: "down"})
		os.Exit(exitCode)
	}()

	gw, err := netcfg.DefaultGateway()
	if err != nil {
		fail("%v", err)
		exitCode = 1
		return
	}
	log.Infof("original default route: gateway=%s interface=%s", gw.IP, gw.Interface)

	// The router resolves and pins the proxies now, before routing changes.
	rt := newRouter(log, gw, &undo, exempt)
	if err := rt.Load(set); err != nil {
		fail("%v", err)
		exitCode = 1
		return
	}

	// The control socket comes up first so a supervisor can watch every event.
	if *controlPort > 0 {
		srv, err := control.Listen(*controlPort, token, rep, func() {
			log.Infof("stop requested over control socket")
			cancel()
		}, func(payload []byte) error {
			newSet, err := rules.Parse(payload)
			if err != nil {
				return err
			}
			if err := rt.Load(newSet); err != nil {
				return err
			}
			log.Infof("rules replaced: %d rules, %d proxies", len(newSet.Rules), len(newSet.Proxies))
			rep.Rules(len(newSet.Rules))
			return nil
		})
		if err != nil {
			fail("%v", err)
			exitCode = 1
			return
		}
		defer srv.Close()
		log.Infof("control socket on %s", srv.Addr())
	}

	dev, err := tun.CreateTUN(*tunName, *mtu)
	if err != nil {
		fail("create TUN %q: %v", *tunName, err)
		exitCode = 1
		return
	}
	undo.Push(func() error { return dev.Close() })
	ifName, err := dev.Name()
	if err != nil {
		fail("TUN name: %v", err)
		exitCode = 1
		return
	}
	log.Infof("TUN %s created", ifName)

	ncfg := netcfg.Config{
		Interface: ifName,
		Addr:      localPrefix,
		Peer:      peer,
		MTU:       *mtu,
		Mode:      netcfg.RouteMode(*routeMode),
	}
	if ncfg.Mode != netcfg.RouteSplit && ncfg.Mode != netcfg.RouteReplace {
		fail("invalid --route-mode %q", *routeMode)
		exitCode = 1
		return
	}
	if err := netcfg.ConfigureInterface(ncfg); err != nil {
		fail("configure %s: %v", ifName, err)
		exitCode = 1
		return
	}

	// The direct resolver must stay reachable outside the tunnel.
	if dnsAddr.IsValid() {
		if err := netcfg.AddHostRoute(dnsAddr, gw, &undo); err != nil {
			// Not fatal: the resolver may be on the local link already, or be a proxy IP.
			log.Warnf("host route for dns %s: %v", dnsAddr, err)
		}
	}

	stats := &engine.Counters{}
	eng, err := engine.New(dev, engine.Options{
		MTU:   uint32(*mtu),
		Dial:  rt.directDial,
		Route: rt.Decide,
		DNS:   dnsHostPort(dnsAddr),
		Logf:  log.Debugf,
		Stats: stats,
	})
	if err != nil {
		fail("%v", err)
		exitCode = 1
		return
	}
	eng.Start()
	undo.Push(func() error { eng.Close(); return nil })

	if err := netcfg.RouteAllViaTun(ncfg, gw, &undo); err != nil {
		fail("route traffic via %s: %v", ifName, err)
		exitCode = 1
		return
	}

	rep.UpWithRules(ifName, rt.Summary(), len(set.Rules), rt.Procmap())
	log.Infof("up: %d rules, all other programs -> %s (process attribution: %s)", len(set.Rules), rt.Summary(), rt.Procmap())

	sigs := make(chan os.Signal, 2)
	signal.Notify(sigs, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)

	ticker := time.NewTicker(*statsEvery)
	defer ticker.Stop()
	parentTicker := time.NewTicker(2 * time.Second)
	defer parentTicker.Stop()

	for {
		select {
		case s := <-sigs:
			log.Infof("signal %v: shutting down", s)
			return
		case <-ctx.Done():
			return
		case <-eng.Done():
			fail("engine stopped unexpectedly")
			exitCode = 1
			return
		case <-ticker.C:
			rep.Stats(stats.Up.Load(), stats.Down.Load())
		case <-parentTicker.C:
			if *parentPID > 0 && !processAlive(*parentPID) {
				log.Infof("parent process %d is gone: shutting down", *parentPID)
				return
			}
		}
	}
}

// loadRules builds the initial rule set: from --rules-file, or from a single
// proxy (--proxy / --proxy-file) as "everything through this proxy".
func loadRules(rulesFile, proxyFile, proxyURL string) (*rules.Set, error) {
	if rulesFile != "" {
		b, err := os.ReadFile(rulesFile)
		if err != nil {
			return nil, fmt.Errorf("read --rules-file: %w", err)
		}
		_ = os.Remove(rulesFile)
		return rules.Parse(b)
	}
	if proxyFile != "" {
		b, err := os.ReadFile(proxyFile)
		if err != nil {
			return nil, fmt.Errorf("read --proxy-file: %w", err)
		}
		_ = os.Remove(proxyFile)
		proxyURL = strings.TrimSpace(string(b))
	}
	if strings.TrimSpace(proxyURL) == "" {
		return nil, fmt.Errorf("no --rules-file and no --proxy given")
	}
	set := &rules.Set{
		Proxies: []rules.Proxy{{ID: 1, URL: proxyURL}},
		Rules:   []rules.Rule{{Process: "*", Action: rules.ActionProxy, Proxy: 1}},
	}
	return set, set.Validate()
}

func dnsHostPort(a netip.Addr) string {
	if !a.IsValid() {
		return ""
	}
	return net.JoinHostPort(a.String(), "53")
}

func processAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}
