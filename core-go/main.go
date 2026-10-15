//go:build darwin || linux

// pbcore routes the TCP traffic of the machine through SOCKS5 or HTTP proxies
// using a TUN interface and a userspace network stack. Which program's
// connections go where is decided by per-program rules (see internal/rules).
//
// It runs in two ways: started by the app for one session (rules from a file,
// exits when the session ends), or as a daemon under launchd (--daemon) that
// stays up and brings the tunnel up and down on "start" and "stop" commands
// over the control socket, so the app needs no administrator password after
// the daemon has been installed once.
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"flag"
	"fmt"
	"net/netip"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"proxybridge/core/internal/control"
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
		tokenFile    = flag.String("token-file", "", "read the control token from this file (daemon mode; created with a random token when missing)")
		daemon       = flag.Bool("daemon", false, "stay up without a tunnel and bring it up/down on \"start\"/\"stop\" control commands")
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
	mode := netcfg.RouteMode(*routeMode)
	if mode != netcfg.RouteSplit && mode != netcfg.RouteReplace {
		fail("invalid --route-mode %q", *routeMode)
		os.Exit(1)
	}

	defaults := sessionConfig{
		TunName:    *tunName,
		TunAddr:    localPrefix,
		Peer:       peer,
		MTU:        *mtu,
		DNS:        dnsAddr,
		RouteMode:  mode,
		StatsEvery: *statsEvery,
	}

	token, err := resolveToken(*controlToken, *tokenFile, *daemon)
	if err != nil {
		fail("%v", err)
		os.Exit(1)
	}
	if *controlToken == "" && *tokenFile == "" {
		log.Infof("control token: %s", token)
	}

	netcfg.Logf = log.Debugf

	if *daemon {
		if *controlPort <= 0 {
			fail("--daemon needs a control port")
			os.Exit(1)
		}
		os.Exit(runDaemon(log, rep, *controlPort, token, defaults))
	}

	set, err := loadRules(*rulesFile, *proxyFile, *proxyURL)
	if err != nil {
		fail("%v", err)
		os.Exit(1)
	}
	cfg := defaults
	cfg.Set = set
	cfg.ParentPID = *parentPID
	cfg.Exempt = []int{os.Getpid(), *parentPID}
	for _, part := range strings.Split(*exemptPIDs, ",") {
		if pid, err := strconv.Atoi(strings.TrimSpace(part)); err == nil && pid > 0 {
			cfg.Exempt = append(cfg.Exempt, pid)
		}
	}
	os.Exit(runOnce(log, rep, *controlPort, token, cfg))
}

// runOnce brings one session up and exits when it ends.
func runOnce(log logger, rep *status.Reporter, port int, token string, cfg sessionConfig) int {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	var (
		mu  sync.Mutex
		cur *session
	)
	// The control socket comes up first so a supervisor can watch every event.
	if port > 0 {
		srv, err := control.Listen(port, control.Options{
			Token: token, Version: version, Reporter: rep,
			OnStop: func() {
				log.Infof("stop requested over control socket")
				cancel()
			},
			OnRules: func(payload []byte) error {
				newSet, err := rules.Parse(payload)
				if err != nil {
					return err
				}
				mu.Lock()
				s := cur
				mu.Unlock()
				if s == nil {
					return fmt.Errorf("tunnel is not up")
				}
				return s.Reload(newSet)
			},
		})
		if err != nil {
			log.Errorf("%v", err)
			rep.Error(err.Error())
			return 1
		}
		defer srv.Close()
		log.Infof("control socket on %s", srv.Addr())
	}

	s, err := bringUp(log, rep, cfg)
	if err != nil {
		return 1
	}
	mu.Lock()
	cur = s
	mu.Unlock()

	sigs := make(chan os.Signal, 2)
	signal.Notify(sigs, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	select {
	case sig := <-sigs:
		log.Infof("signal %v: shutting down", sig)
		s.Stop()
	case <-ctx.Done():
		s.Stop()
	case <-s.Done():
	}
	if s.Err() != nil {
		return 1
	}
	return 0
}

// runDaemon keeps the process alive without a tunnel; "start" and "stop" over
// the control socket bring sessions up and down. launchd keeps it running.
func runDaemon(log logger, rep *status.Reporter, port int, token string, defaults sessionConfig) int {
	var (
		mu  sync.Mutex
		cur *session
	)
	stopCurrent := func() {
		mu.Lock()
		s := cur
		cur = nil
		mu.Unlock()
		if s != nil {
			s.Stop()
		}
	}
	var srv *control.Server
	srv, err := control.Listen(port, control.Options{
		Token: token, Version: version, Reporter: rep,
		OnStop: func() {
			log.Infof("stop requested over control socket")
			stopCurrent()
			srv.ResetStop()
		},
		OnRules: func(payload []byte) error {
			newSet, err := rules.Parse(payload)
			if err != nil {
				return err
			}
			mu.Lock()
			s := cur
			mu.Unlock()
			if s == nil {
				return fmt.Errorf("tunnel is not up")
			}
			return s.Reload(newSet)
		},
		OnStart: func(payload []byte) error {
			cfg, err := parseStartRequest(payload, defaults)
			if err != nil {
				return err
			}
			stopCurrent()
			log.Infof("start requested: %d rules, %d proxies, parent pid %d", len(cfg.Set.Rules), len(cfg.Set.Proxies), cfg.ParentPID)
			s, err := bringUp(log, rep, cfg)
			if err != nil {
				return err
			}
			mu.Lock()
			cur = s
			mu.Unlock()
			go func() {
				<-s.Done()
				mu.Lock()
				if cur == s {
					cur = nil
				}
				mu.Unlock()
			}()
			return nil
		},
	})
	if err != nil {
		log.Errorf("%v", err)
		rep.Error(err.Error())
		return 1
	}
	defer srv.Close()
	log.Infof("pbcore %s daemon: control socket on %s, waiting for start", version, srv.Addr())
	rep.Emit(status.Line{Status: "idle"})

	sigs := make(chan os.Signal, 2)
	signal.Notify(sigs, syscall.SIGINT, syscall.SIGTERM)
	sig := <-sigs
	log.Infof("signal %v: shutting down", sig)
	stopCurrent()
	return 0
}

// resolveToken picks the control token: the flag, the token file (created
// with a random token in daemon mode when missing) or a fresh random one.
func resolveToken(flagToken, tokenFile string, daemon bool) (string, error) {
	if flagToken != "" {
		return flagToken, nil
	}
	if tokenFile != "" {
		b, err := os.ReadFile(tokenFile)
		if err == nil && strings.TrimSpace(string(b)) != "" {
			return strings.TrimSpace(string(b)), nil
		}
		if !daemon {
			return "", fmt.Errorf("read --token-file: %v", err)
		}
		t, err := randomToken()
		if err != nil {
			return "", err
		}
		if err := writeTokenFile(tokenFile, t); err != nil {
			return "", err
		}
		return t, nil
	}
	return randomToken()
}

func randomToken() (string, error) {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("random token: %v", err)
	}
	return hex.EncodeToString(b), nil
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

func processAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}
