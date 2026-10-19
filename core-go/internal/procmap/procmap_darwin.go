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
	"sync"
	"time"

	"golang.org/x/sys/unix"
)

// Resolver maps TCP connections to the executables that own them.
type Resolver struct {
	logf func(format string, args ...any)

	mu       sync.Mutex
	layout   Layout
	useNet   bool // netstat fallback instead of the sysctl parser
	conns    []Conn
	connsAt  time.Time
	udpConns []Conn
	udpAt    time.Time
	exeCache map[int]exeEntry
}

type exeEntry struct {
	path string
	at   time.Time
}

const (
	// A fresh table is reused for this long: bursts of new connections
	// (a browser opening a page) share one read.
	listTTL = 150 * time.Millisecond
	exeTTL  = 2 * time.Minute
)

// New creates a resolver. It calibrates the pcblist parser against a
// connection the process makes to itself; when that fails it falls back to
// "netstat -anv", which prints the owning pid of every socket.
func New(logf func(string, ...any)) *Resolver {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	r := &Resolver{logf: logf, exeCache: make(map[int]exeEntry), layout: DefaultLayout}
	if err := r.selfTest(); err != nil {
		r.logf("procmap: pcblist self-test failed (%v): using netstat", err)
		r.useNet = true
		if err := r.netstatSelfTest(); err != nil {
			r.logf("procmap: netstat self-test failed too (%v): programs cannot be told apart, only the catch-all rule applies", err)
		}
	} else {
		r.logf("procmap: pcblist self-test ok (layout ports@%d addrs@%d/%d pid@%d kinds %#x/%#x)",
			r.layout.Fport, r.layout.Faddr4, r.layout.Laddr4, r.layout.Pid, r.layout.InpKind, r.layout.SoKind)
	}
	return r
}

// Method names the attribution method for the status line.
func (r *Resolver) Method() string {
	if r.useNet {
		return "netstat"
	}
	return "pcblist"
}

// UsesLsof is kept for API compatibility: true when the slow fallback is active.
func (r *Resolver) UsesLsof() bool { return r.useNet }

func selfConnection() (local, remote netip.AddrPort, closeAll func(), err error) {
	ln, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return local, remote, nil, err
	}
	c, err := net.Dial("tcp4", ln.Addr().String())
	if err != nil {
		ln.Close()
		return local, remote, nil, err
	}
	srv, err := ln.Accept()
	if err != nil {
		c.Close()
		ln.Close()
		return local, remote, nil, err
	}
	local = c.LocalAddr().(*net.TCPAddr).AddrPort()
	remote = c.RemoteAddr().(*net.TCPAddr).AddrPort()
	return local, remote, func() { srv.Close(); c.Close(); ln.Close() }, nil
}

func (r *Resolver) selfTest() error {
	local, remote, closeAll, err := selfConnection()
	if err != nil {
		return err
	}
	defer closeAll()

	b, err := unix.SysctlRaw("net.inet.tcp.pcblist_n")
	if err != nil {
		return fmt.Errorf("sysctl net.inet.tcp.pcblist_n: %w", err)
	}
	lay, ok := Calibrate(b, local, remote, os.Getpid())
	if !ok {
		return fmt.Errorf("own connection %s -> %s not recognizable in %d bytes / %d records", local, remote, len(b), len(Records(b)))
	}
	r.layout = lay
	path, err := r.exePath(os.Getpid())
	if err != nil {
		return fmt.Errorf("exe path: %w", err)
	}
	self, _ := os.Executable()
	if self != "" && path != self {
		return fmt.Errorf("exe path %q, want %q", path, self)
	}
	return nil
}

func (r *Resolver) netstatSelfTest() error {
	local, remote, closeAll, err := selfConnection()
	if err != nil {
		return err
	}
	defer closeAll()
	conns, err := readNetstat("tcp")
	if err != nil {
		return err
	}
	pid, ok := Find(conns, local, remote)
	if !ok {
		return fmt.Errorf("own connection not listed (%d entries)", len(conns))
	}
	if pid != os.Getpid() {
		return fmt.Errorf("own connection attributed to pid %d", pid)
	}
	return nil
}

func (r *Resolver) readTable() ([]Conn, error) {
	if r.useNet {
		return readNetstat("tcp")
	}
	b, err := unix.SysctlRaw("net.inet.tcp.pcblist_n")
	if err != nil {
		return nil, fmt.Errorf("sysctl net.inet.tcp.pcblist_n: %w", err)
	}
	return ParsePcblistLayout(b, r.layout), nil
}

// readUDPTable lists UDP sockets. The records have the same layout as the TCP
// ones (xinpcb_n / xsocket_n), so the calibrated layout applies.
func (r *Resolver) readUDPTable() ([]Conn, error) {
	if r.useNet {
		return readNetstat("udp")
	}
	b, err := unix.SysctlRaw("net.inet.udp.pcblist_n")
	if err != nil {
		return nil, fmt.Errorf("sysctl net.inet.udp.pcblist_n: %w", err)
	}
	return ParsePcblistLayout(b, r.layout), nil
}

// readNetstat runs "netstat -anv -p tcp|udp", which prints the owning pid of every socket.
func readNetstat(proto string) ([]Conn, error) {
	out, err := exec.Command("netstat", "-anv", "-p", proto).Output()
	if err != nil {
		return nil, fmt.Errorf("netstat: %w", err)
	}
	return ParseNetstat(string(out))
}

// LookupUDP returns the owner of the UDP socket bound to local (the remote
// side is usually unknown for UDP sockets and only used as a tie breaker).
func (r *Resolver) LookupUDP(local, remote netip.AddrPort) (pid int, exe string, ok bool) {
	for attempt := 0; attempt < 2; attempt++ {
		r.mu.Lock()
		if attempt > 0 || time.Since(r.udpAt) > listTTL {
			conns, err := r.readUDPTable()
			if err != nil {
				r.mu.Unlock()
				r.logf("procmap: %v", err)
				return 0, "", false
			}
			r.udpConns = conns
			r.udpAt = time.Now()
		}
		conns := r.udpConns
		r.mu.Unlock()
		if p, found := Find(conns, local, remote); found {
			pid = p
			ok = true
			break
		}
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

// Lookup returns the pid and executable path of the process that owns the TCP
// connection local -> remote. A connection that just started may not be listed
// on the first read, so one immediate retry is made.
func (r *Resolver) Lookup(local, remote netip.AddrPort) (pid int, exe string, ok bool) {
	for attempt := 0; attempt < 2; attempt++ {
		r.mu.Lock()
		if attempt > 0 || time.Since(r.connsAt) > listTTL {
			conns, err := r.readTable()
			if err != nil {
				r.mu.Unlock()
				r.logf("procmap: %v", err)
				return 0, "", false
			}
			r.conns = conns
			r.connsAt = time.Now()
		}
		conns := r.conns
		r.mu.Unlock()
		if p, found := Find(conns, local, remote); found {
			pid = p
			ok = true
			break
		}
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
