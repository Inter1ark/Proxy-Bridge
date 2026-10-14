//go:build darwin

package main

import (
	"net"
	"syscall"

	"golang.org/x/sys/unix"
)

// boundDialer returns a dialer whose sockets are scoped to the interface of the
// original default route (IP_BOUND_IF). While the tunnel is up, every other
// route points at the TUN, so this is what keeps the core's own upstream
// connections (to proxies and to direct destinations) out of its own tunnel.
func boundDialer(ifName string) *net.Dialer {
	d := &net.Dialer{}
	iface, err := net.InterfaceByName(ifName)
	if err != nil || iface.Index <= 0 {
		return d
	}
	idx := iface.Index
	d.Control = func(network, address string, c syscall.RawConn) error {
		var serr error
		err := c.Control(func(fd uintptr) {
			serr = unix.SetsockoptInt(int(fd), unix.IPPROTO_IP, unix.IP_BOUND_IF, idx)
		})
		if err != nil {
			return err
		}
		return serr
	}
	return d
}
