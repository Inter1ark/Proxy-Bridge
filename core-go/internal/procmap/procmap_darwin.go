//go:build darwin

package procmap

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"net"
	"net/netip"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.org/x/sys/unix"
)

// Resolver maps TCP connections to the executables that own them.
type Resolver struct {
	logf func(format string, args ...any)

	mu       sync.Mutex
	conns    []Conn
	connsAt  time.Time
	useLsof  bool
	exeCache map[int]exeEntry
}

type exeEntry struct {
	path string
	at   time.Time
}

const (
	// A fresh pcblist is reused for this long: bursts of new connections
	// (a browser opening a page) share one sysctl call.
	listTTL = 150 * time.Millisecond
	exeTTL  = 2 * time.Minute
)

// New creates a resolver and checks that the pcblist parser works on this
// kernel by looking up a connection the process makes to itself. When the
// check fails the resolver falls back to lsof, which is slower but always right.
func New(logf func(string, ...any)) *Resolver {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	r := &Resolver{logf: logf, exeCache: make(map[int]exeEntry)}
	if err := r.selfTest(); err != nil {
		r.logf("procmap: pcblist self-test failed (%v): using lsof", err)
		r.useLsof = true
	} else {
		r.logf("procmap: pcblist self-test ok")
	}
	return r
}

// UsesLsof reports whether the slow fallback is active.
func (r *Resolver) UsesLsof() bool { return r.useLsof }

func (r *Resolver) selfTest() error {
	ln, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer ln.Close()
	c, err := net.Dial("tcp4", ln.Addr().String())
	if err != nil {
		return err
	}
	defer c.Close()
	srv, err := ln.Accept()
	if err != nil {
		return err
	}
	defer srv.Close()

	local := c.LocalAddr().(*net.TCPAddr).AddrPort()
	remote := c.RemoteAddr().(*net.TCPAddr).AddrPort()
	conns, err := readPcblist()
	if err != nil {
		return err
	}
	pid, ok := Find(conns, local, remote)
	if !ok {
		return fmt.Errorf("own connection %s -> %s not listed (%d entries)", local, remote, len(conns))
	}
	if pid != os.Getpid() {
		return fmt.Errorf("own connection attributed to pid %d, want %d", pid, os.Getpid())
	}
	path, err := r.exePath(pid)
	if err != nil {
		return fmt.Errorf("exe path: %w", err)
	}
	self, _ := os.Executable()
	if self != "" && path != self {
		return fmt.Errorf("exe path %q, want %q", path, self)
	}
	return nil
}

func readPcblist() ([]Conn, error) {
	b, err := unix.SysctlRaw("net.inet.tcp.pcblist_n")
	if err != nil {
		return nil, fmt.Errorf("sysctl net.inet.tcp.pcblist_n: %w", err)
	}
	return ParsePcblist(b), nil
}

// Lookup returns the pid and executable path of the process that owns the TCP
// connection local -> remote. A connection that just started may not be listed
// on the first read, so one immediate retry is made.
func (r *Resolver) Lookup(local, remote netip.AddrPort) (pid int, exe string, ok bool) {
	if r.useLsof {
		pid, ok = r.lsofPid(local)
	} else {
		pid, ok = r.sysctlPid(local, remote)
	}
	if !ok {
		return 0, "", false
	}
	exe, err := r.exePath(pid)
	if err != nil {
		r.logf("procmap: exe of pid %d: %v", pid, err)
		return pid, "", true
	}
	return pid, exe, true
}

func (r *Resolver) sysctlPid(local, remote netip.AddrPort) (int, bool) {
	for attempt := 0; attempt < 2; attempt++ {
		r.mu.Lock()
		stale := attempt > 0 || time.Since(r.connsAt) > listTTL
		if stale {
			conns, err := readPcblist()
			if err != nil {
				r.mu.Unlock()
				r.logf("procmap: %v", err)
				return 0, false
			}
			r.conns = conns
			r.connsAt = time.Now()
		}
		conns := r.conns
		r.mu.Unlock()
		if pid, ok := Find(conns, local, remote); ok {
			return pid, true
		}
	}
	return 0, false
}

func (r *Resolver) lsofPid(local netip.AddrPort) (int, bool) {
	// lsof -nP -a -iTCP -iTCP@10.255.0.2:51000 -Fp
	out, err := exec.Command("lsof", "-nP", "-a", "-iTCP", "-iTCP@"+local.String(), "-Fp").Output()
	if err != nil {
		return 0, false
	}
	for _, line := range strings.Split(string(out), "\n") {
		if strings.HasPrefix(line, "p") {
			if pid, err := strconv.Atoi(strings.TrimSpace(line[1:])); err == nil && pid > 0 {
				return pid, true
			}
		}
	}
	return 0, false
}

// exePath returns the executable of a process from "kern.procargs2": the
// buffer starts with argc (int32) followed by the NUL-terminated executable path.
func (r *Resolver) exePath(pid int) (string, error) {
	r.mu.Lock()
	if e, ok := r.exeCache[pid]; ok && time.Since(e.at) < exeTTL {
		r.mu.Unlock()
		return e.path, nil
	}
	r.mu.Unlock()

	path, err := procArgsExe(pid)
	if err != nil {
		// Processes of other users or protected system processes hide their
		// arguments; "ps" still knows the command path.
		out, psErr := exec.Command("ps", "-o", "comm=", "-p", strconv.Itoa(pid)).Output()
		if psErr != nil || len(bytes.TrimSpace(out)) == 0 {
			return "", err
		}
		path = string(bytes.TrimSpace(out))
	}
	r.mu.Lock()
	if len(r.exeCache) > 4096 {
		r.exeCache = make(map[int]exeEntry)
	}
	r.exeCache[pid] = exeEntry{path: path, at: time.Now()}
	r.mu.Unlock()
	return path, nil
}

func procArgsExe(pid int) (string, error) {
	b, err := unix.SysctlRaw("kern.procargs2", pid)
	if err != nil {
		return "", fmt.Errorf("sysctl kern.procargs2 %d: %w", pid, err)
	}
	if len(b) < 5 {
		return "", fmt.Errorf("kern.procargs2 %d: short buffer", pid)
	}
	_ = binary.LittleEndian.Uint32(b[:4]) // argc
	rest := b[4:]
	end := bytes.IndexByte(rest, 0)
	if end <= 0 {
		return "", fmt.Errorf("kern.procargs2 %d: no executable path", pid)
	}
	return string(rest[:end]), nil
}
