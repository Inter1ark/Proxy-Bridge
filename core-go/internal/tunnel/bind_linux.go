//go:build linux

package tunnel

import (
	"net"
	"syscall"

	"golang.org/x/sys/unix"
)

// boundDialer returns a dialer whose sockets leave through the interface of the
// original default route (SO_BINDTODEVICE), so the core's own upstream
// connections never enter its own tunnel.
func boundDialer(ifName string) *net.Dialer {
	d := &net.Dialer{}
	if ifName == "" {
		return d
	}
	d.Control = func(network, address string, c syscall.RawConn) error {
		var serr error
		err := c.Control(func(fd uintptr) {
			serr = unix.BindToDevice(int(fd), ifName)
		})
		if err != nil {
			return err
		}
		return serr
	}
	return d
}
