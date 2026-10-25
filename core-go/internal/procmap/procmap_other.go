//go:build !darwin

package procmap

import "net/netip"

// LookupFunc answers who owns a flow: the host supplies it on platforms where the
// core cannot read the socket table itself (Android asks the system for the
// owning uid and maps it to a package name). ok=false means unknown.
type LookupFunc func(proto int, local, remote netip.AddrPort) (id int, name string, ok bool)

var hook LookupFunc

// SetHook installs the host-supplied lookup (nil removes it).
func SetHook(f LookupFunc) { hook = f }

// Resolver attributes flows through the host hook when one is installed;
// otherwise every connection is unknown and only "*" rules apply.
type Resolver struct{}

// New creates the resolver.
func New(logf func(string, ...any)) *Resolver {
	if logf != nil && hook == nil {
		logf("procmap: process attribution is not available on this platform")
	}
	return &Resolver{}
}

// Method names the attribution method.
func (r *Resolver) Method() string {
	if hook != nil {
		return "host"
	}
	return "none"
}

// UsesLsof is always false here.
func (r *Resolver) UsesLsof() bool { return false }

// Lookup asks the host for the owner of a TCP flow.
func (r *Resolver) Lookup(local, remote netip.AddrPort) (int, string, bool) {
	if hook == nil {
		return 0, "", false
	}
	return hook(6, local, remote)
}

// LookupUDP asks the host for the owner of a UDP flow.
func (r *Resolver) LookupUDP(local, remote netip.AddrPort) (int, string, bool) {
	if hook == nil {
		return 0, "", false
	}
	return hook(17, local, remote)
}
