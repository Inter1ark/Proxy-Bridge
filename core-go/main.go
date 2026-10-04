//go:build darwin || linux

// pbcore routes all TCP traffic of the machine through one SOCKS5 or HTTP
// proxy using a TUN interface and a userspace network stack.
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
	"proxybridge/core/internal/proxy"
	"proxybridge/core/internal/status"
)

const version = "3.2.0"

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
		proxyURL     = flag.String("proxy", "", "upstream proxy: socks5://user:pass@host:port or http://user:pass@host:port")
		proxyFile    = flag.String("proxy-file", "", "read the proxy URL from this file instead of --proxy; the file is deleted after reading")
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

	if *proxyFile != "" {
		b, err := os.ReadFile(*proxyFile)
		if err != nil {
			fail("read --proxy-file: %v", err)
			os.Exit(1)
		}
		_ = os.Remove(*proxyFile)
		*proxyURL = strings.TrimSpace(string(b))
	}

	cfg, err := proxy.Parse(*proxyURL)
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

	// Resolve the proxy host now, before routing changes, and keep only the IP.
	proxyIP, err := resolveIPv4(cfg.Host)
	if err != nil {
		fail("resolve proxy host %q: %v", cfg.Host, err)
		os.Exit(1)
	}
	serverAddr := net.JoinHostPort(proxyIP.String(), strconv.Itoa(cfg.Port))
	log.Infof("proxy %s -> %s", cfg.Redacted(), serverAddr)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// The control socket comes up first so a supervisor can watch every event.
	if *controlPort > 0 {
		srv, err := control.Listen(*controlPort, token, rep, func() {
			log.Infof("stop requested over control socket")
			cancel()
		})
		if err != nil {
			fail("%v", err)
			os.Exit(1)
		}
		defer srv.Close()
		log.Infof("control socket on %s", srv.Addr())
	}

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

	gw, err := netcfg.DefaultGateway()
	if err != nil {
		fail("%v", err)
		exitCode = 1
		return
	}
	log.Infof("original default route: gateway=%s interface=%s", gw.IP, gw.Interface)

	// The proxy server and the direct resolver must stay reachable outside the tunnel.
	if err := netcfg.AddHostRoute(proxyIP, gw, &undo); err != nil {
		fail("host route for proxy %s: %v", proxyIP, err)
		exitCode = 1
		return
	}
	if dnsAddr.IsValid() && dnsAddr != proxyIP {
		if err := netcfg.AddHostRoute(dnsAddr, gw, &undo); err != nil {
			// Not fatal: the resolver may be on the local link already.
			log.Warnf("host route for dns %s: %v", dnsAddr, err)
		}
	}

	stats := &engine.Counters{}
	dialer := proxy.NewDialer(cfg, serverAddr, 15*time.Second)
	eng, err := engine.New(dev, engine.Options{
		MTU:   uint32(*mtu),
		Dial:  dialer.Dial,
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

	rep.Up(ifName, cfg.Redacted())
	log.Infof("up: all TCP traffic now goes through %s", cfg.Redacted())

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

func dnsHostPort(a netip.Addr) string {
	if !a.IsValid() {
		return ""
	}
	return net.JoinHostPort(a.String(), "53")
}

func resolveIPv4(host string) (netip.Addr, error) {
	if ip, err := netip.ParseAddr(host); err == nil {
		if !ip.Is4() {
			return netip.Addr{}, fmt.Errorf("IPv6 proxy addresses are not supported")
		}
		return ip, nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip4", host)
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

func processAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}
