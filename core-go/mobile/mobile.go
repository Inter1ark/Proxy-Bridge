//go:build linux

// Package pbmobile is the Android face of pbcore (built into an .aar with gomobile).
// The app opens the TUN through VpnService, hands over the file descriptor and the
// same start request the desktop helper takes (proxies, rules, udp, dns options);
// rule "process" names are Android package names. The app answers who owns a flow
// (ConnectivityManager.getConnectionOwnerUid) and receives log and status lines.
package pbmobile

import (
	"bytes"
	"errors"
	"fmt"
	"net/netip"
	"sync"
	"time"

	"golang.zx2c4.com/wireguard/tun"

	"proxybridge/core/internal/netcfg"
	"proxybridge/core/internal/procmap"
	"proxybridge/core/internal/rules"
	"proxybridge/core/internal/status"
	"proxybridge/core/internal/tunnel"
)

// Version is set at build time (-ldflags "-X proxybridge/core/mobile.Version=...").
var Version = "dev"

// Log levels passed to Callbacks.Log.
const (
	LevelDebug = 0
	LevelInfo  = 1
	LevelWarn  = 2
	LevelError = 3
)

// Callbacks is implemented by the Android app.
type Callbacks interface {
	// Log receives one log line.
	Log(level int, msg string)
	// OwnerUid returns the uid owning the flow (proto 6 tcp / 17 udp), or -1 when unknown.
	OwnerUid(proto int, srcIP string, srcPort int, dstIP string, dstPort int) int
	// PackageForUid maps a uid to its package name ("" when unknown).
	PackageForUid(uid int) string
	// Status receives the JSON status lines (up, down, stats, error).
	Status(line string)
}

var (
	mu  sync.Mutex
	cur *tunnel.Session
	cb  Callbacks
	rep *status.Reporter
)

type hostLogger struct{}

func (hostLogger) logf(level int, f string, a ...any) {
	if c := cb; c != nil {
		c.Log(level, fmt.Sprintf(f, a...))
	}
}
func (l hostLogger) Debugf(f string, a ...any) { l.logf(LevelDebug, f, a...) }
func (l hostLogger) Infof(f string, a ...any)  { l.logf(LevelInfo, f, a...) }
func (l hostLogger) Warnf(f string, a ...any)  { l.logf(LevelWarn, f, a...) }
func (l hostLogger) Errorf(f string, a ...any) { l.logf(LevelError, f, a...) }

// statusWriter turns the reporter's byte stream into one callback per line.
type statusWriter struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (w *statusWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.buf.Write(p)
	for {
		i := bytes.IndexByte(w.buf.Bytes(), '\n')
		if i < 0 {
			break
		}
		line := string(w.buf.Next(i + 1))
		if c := cb; c != nil {
			c.Status(line[:len(line)-1])
		}
	}
	return len(p), nil
}

func lookup(proto int, local, remote netip.AddrPort) (int, string, bool) {
	c := cb
	if c == nil {
		return 0, "", false
	}
	uid := c.OwnerUid(proto, local.Addr().String(), int(local.Port()), remote.Addr().String(), int(remote.Port()))
	if uid < 0 {
		return 0, "", false
	}
	return uid, c.PackageForUid(uid), true
}

// Start brings the tunnel up on the VpnService file descriptor (ownership passes to
// the core: detach it on the Java side). requestJSON is the start request: proxies,
// rules (process = package name, "*" for everything else), udp, dns_via_proxy, dns.
func Start(fd int, requestJSON string, c Callbacks) error {
	if c == nil {
		return errors.New("callbacks are required")
	}
	mu.Lock()
	defer mu.Unlock()
	if cur != nil {
		return errors.New("already running")
	}
	cb = c
	procmap.SetHook(lookup)
	netcfg.Logf = hostLogger{}.Debugf

	defaults := tunnel.Config{
		TunAddr:    netip.MustParsePrefix("10.255.0.2/24"),
		Peer:       netip.MustParseAddr("10.255.0.1"),
		MTU:        1500,
		DNS:        netip.MustParseAddr("1.1.1.1"),
		RouteMode:  netcfg.RouteSplit,
		StatsEvery: 2 * time.Second,
	}
	cfg, err := tunnel.ParseRequest([]byte(requestJSON), defaults)
	if err != nil {
		return err
	}
	dev, name, err := tun.CreateUnmonitoredTUNFromFD(fd)
	if err != nil {
		return fmt.Errorf("attach tun fd %d: %w", fd, err)
	}
	cfg.Device = dev
	cfg.TunName = name
	cfg.ParentPID = 0

	rep = status.New()
	rep.Attach(&statusWriter{})
	s, err := tunnel.BringUp(hostLogger{}, rep, cfg)
	if err != nil {
		return err
	}
	cur = s
	go func() {
		<-s.Done()
		mu.Lock()
		if cur == s {
			cur = nil
		}
		mu.Unlock()
	}()
	return nil
}

// Stop tears the tunnel down and waits for the cleanup.
func Stop() {
	mu.Lock()
	s := cur
	cur = nil
	mu.Unlock()
	if s != nil {
		s.Stop()
	}
}

// Reload replaces the rule set of the running tunnel (same JSON as the "rules" command).
func Reload(rulesJSON string) error {
	mu.Lock()
	s := cur
	mu.Unlock()
	if s == nil {
		return errors.New("tunnel is not up")
	}
	set, err := rules.Parse([]byte(rulesJSON))
	if err != nil {
		return err
	}
	return s.Reload(set)
}

// Running reports whether a tunnel is up.
func Running() bool {
	mu.Lock()
	defer mu.Unlock()
	return cur != nil
}

// CoreVersion returns the core version string.
func CoreVersion() string { return Version }
