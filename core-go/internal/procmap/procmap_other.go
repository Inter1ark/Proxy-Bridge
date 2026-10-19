//go:build !darwin

package procmap

import "net/netip"

// Resolver is a stub on platforms without process attribution: every
// connection is reported as unknown, so only "*" rules apply.
type Resolver struct{}

// New creates the stub resolver.
func New(logf func(string, ...any)) *Resolver {
	if logf != nil {
		logf("procmap: process attribution is not available on this platform")
	}
	return &Resolver{}
}

// Method names the attribution method.
func (r *Resolver) Method() string { return "none" }

// UsesLsof is always false here.
func (r *Resolver) UsesLsof() bool { return false }

// Lookup never finds an owner.
func (r *Resolver) Lookup(local, remote netip.AddrPort) (int, string, bool) {
	return 0, "", false
}

// LookupUDP is not available on this platform.
func (r *Resolver) LookupUDP(local, remote netip.AddrPort) (int, string, bool) { return 0, "", false }
