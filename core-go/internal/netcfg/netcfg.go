//go:build darwin || linux

// Package netcfg configures the TUN interface address and the host routing
// table using the system route tools, and undoes the changes on exit.
package netcfg

import (
	"bytes"
	"fmt"
	"net/netip"
	"os/exec"
	"strings"
)

// Gateway is the default route that was active before the tunnel came up.
type Gateway struct {
	// IP is the next hop. It is invalid when the default route is a direct link.
	IP netip.Addr
	// Interface is the outgoing interface name (may be empty on Linux if unknown).
	Interface string
}

// RouteMode selects how all traffic is pointed at the TUN.
type RouteMode string

const (
	// RouteSplit adds 0.0.0.0/1 and 128.0.0.0/1 through the TUN. The original
	// default route stays in place, so a crash leaves the system online.
	RouteSplit RouteMode = "split"
	// RouteReplace deletes the default route and installs the TUN as default.
	// The original route is restored on exit.
	RouteReplace RouteMode = "replace"
)

// Config describes the tunnel addressing.
type Config struct {
	Interface string
	Addr      netip.Prefix // local address with prefix, e.g. 10.255.0.2/24
	Peer      netip.Addr   // point-to-point peer, e.g. 10.255.0.1
	MTU       int
	Mode      RouteMode
}

// Logf receives every command executed (set by the caller for debugging).
var Logf = func(format string, args ...any) {}

func run(name string, args ...string) (string, error) {
	Logf("exec: %s %s", name, strings.Join(args, " "))
	cmd := exec.Command(name, args...)
	var out, errb bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errb
	if err := cmd.Run(); err != nil {
		msg := strings.TrimSpace(errb.String())
		if msg == "" {
			msg = strings.TrimSpace(out.String())
		}
		return out.String(), fmt.Errorf("%s %s: %v: %s", name, strings.Join(args, " "), err, msg)
	}
	return out.String(), nil
}

// Undo is a stack of cleanup actions executed in reverse order.
type Undo struct {
	steps []func() error
}

// Push registers a cleanup step.
func (u *Undo) Push(f func() error) { u.steps = append(u.steps, f) }

// Run executes all steps in reverse order and returns the first error.
func (u *Undo) Run() error {
	var first error
	for i := len(u.steps) - 1; i >= 0; i-- {
		if err := u.steps[i](); err != nil {
			Logf("cleanup: %v", err)
			if first == nil {
				first = err
			}
		}
	}
	u.steps = nil
	return first
}
