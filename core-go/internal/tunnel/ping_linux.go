//go:build linux

package tunnel

import (
	"errors"
	"net/netip"
)

// pingThrough is not implemented on Linux (Android routes ICMP itself).
func pingThrough(ifName string, dst netip.Addr, msg []byte) ([]byte, error) {
	return nil, errors.New("ping: not supported on this platform")
}
