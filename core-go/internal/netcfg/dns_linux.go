//go:build linux

package netcfg

import "net/netip"

// SetSystemDNS is a no-op on Linux: names are resolved by the application itself (or by a
// local stub on 127.0.0.53 whose upstream queries enter the tunnel routes).
func SetSystemDNS(ip netip.Addr, undo *Undo) error { return nil }

// RestoreSystemDNS is a no-op on Linux.
func RestoreSystemDNS() error { return nil }
