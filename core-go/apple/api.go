//go:build darwin

// The iOS face of pbcore: a C archive (libpbcore.a) linked into the packet tunnel
// extension and the app. The extension hands over the utun descriptor and the start
// request (the same JSON as the desktop helper and Android); log and status lines go
// back through a C callback. See apple/Makefile and apple/pbcore.h.
package main

// #include <stdlib.h>
// static void callLogger(void *func, int level, const char *msg)
// {
// 	((void(*)(int, const char *))func)(level, msg);
// }
import "C"

import (
	"bytes"
	"fmt"
	"net/netip"
	"os"
	"runtime/debug"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/unix"
	"golang.zx2c4.com/wireguard/tun"

	"proxybridge/core/internal/check"
	"proxybridge/core/internal/netcfg"
	"proxybridge/core/internal/rules"
	"proxybridge/core/internal/status"
	"proxybridge/core/internal/tunnel"
)

var version = "dev"

var (
	mu      sync.Mutex
	cur     *tunnel.Session
	logFunc unsafe.Pointer
)

func emit(level int, msg string) {
	if uintptr(logFunc) == 0 {
		return
	}
	cs := C.CString(msg)
	C.callLogger(logFunc, C.int(level), cs)
	C.free(unsafe.Pointer(cs))
}

type hostLogger struct{}

func (hostLogger) Debugf(f string, a ...any) { emit(0, fmt.Sprintf(f, a...)) }
func (hostLogger) Infof(f string, a ...any)  { emit(1, fmt.Sprintf(f, a...)) }
func (hostLogger) Warnf(f string, a ...any)  { emit(2, fmt.Sprintf(f, a...)) }
func (hostLogger) Errorf(f string, a ...any) { emit(3, fmt.Sprintf(f, a...)) }

// statusWriter forwards the reporter's JSON lines with level 9.
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
		emit(9, line[:len(line)-1])
	}
	return len(p), nil
}

func main() {}

//export pbSetLogger
func pbSetLogger(fn uintptr) {
	logFunc = unsafe.Pointer(fn)
}

//export pbVersion
func pbVersion() *C.char { return C.CString(version) }

// pbStart brings the tunnel up on the utun descriptor. bindIf is the physical interface
// the core's own sockets are scoped to. Returns 0, or -1 with the reason logged.
//
//export pbStart
func pbStart(request *C.char, tunFd C.int, bindIf *C.char) C.int {
	mu.Lock()
	defer mu.Unlock()
	if cur != nil {
		emit(3, "already running")
		return -1
	}
	// Keep the extension well inside the memory limit of network extensions.
	debug.SetGCPercent(20)
	debug.SetMemoryLimit(40 << 20)
	netcfg.Logf = hostLogger{}.Debugf

	dupFd, err := unix.Dup(int(tunFd))
	if err != nil {
		emit(3, "dup tun fd: "+err.Error())
		return -1
	}
	if err := unix.SetNonblock(dupFd, true); err != nil {
		emit(3, "tun fd nonblocking: "+err.Error())
		unix.Close(dupFd)
		return -1
	}
	dev, err := tun.CreateTUNFromFile(os.NewFile(uintptr(dupFd), "/dev/tun"), 0)
	if err != nil {
		emit(3, "attach tun: "+err.Error())
		unix.Close(dupFd)
		return -1
	}
	defaults := tunnel.Config{
		TunAddr:       netip.MustParsePrefix("10.255.0.2/24"),
		Peer:          netip.MustParseAddr("10.255.0.1"),
		MTU:           1500,
		DNS:           netip.MustParseAddr("1.1.1.1"),
		RouteMode:     netcfg.RouteSplit,
		StatsEvery:    2 * time.Second,
		BindInterface: C.GoString(bindIf),
	}
	cfg, err := tunnel.ParseRequest([]byte(C.GoString(request)), defaults)
	if err != nil {
		emit(3, "request: "+err.Error())
		dev.Close()
		return -1
	}
	cfg.Device = dev
	cfg.TunName = "utun"
	cfg.ParentPID = 0
	rep := status.New()
	rep.Attach(&statusWriter{})
	s, err := tunnel.BringUp(hostLogger{}, rep, cfg)
	if err != nil {
		return -1
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
	return 0
}

//export pbStop
func pbStop() {
	mu.Lock()
	s := cur
	cur = nil
	mu.Unlock()
	if s != nil {
		s.Stop()
	}
}

//export pbRunning
func pbRunning() C.int {
	mu.Lock()
	defer mu.Unlock()
	if cur != nil {
		return 1
	}
	return 0
}

// pbReload replaces the rule set (the "rules" JSON). Returns 0 or -1.
//
//export pbReload
func pbReload(rulesJSON *C.char) C.int {
	mu.Lock()
	s := cur
	mu.Unlock()
	if s == nil {
		return -1
	}
	set, err := rules.Parse([]byte(C.GoString(rulesJSON)))
	if err != nil {
		emit(3, "rules: "+err.Error())
		return -1
	}
	if err := s.Reload(set); err != nil {
		emit(3, "rules: "+err.Error())
		return -1
	}
	return 0
}

// pbCheckProxy returns the JSON check result (caller frees it with free()).
//
//export pbCheckProxy
func pbCheckProxy(rawURL, lang *C.char) *C.char {
	return C.CString(check.JSON(C.GoString(rawURL), C.GoString(lang)))
}
