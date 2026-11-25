//go:build darwin

package tunnel

import (
	"net"
	"sync"
	"syscall"
	"time"

	"golang.org/x/sys/unix"
)

// ifIndex resolves an interface name to its current index. A VPN client that reconnects
// destroys its utun and creates a new one under the same name with another index, so the
// index is looked up again every few seconds instead of once: sockets scoped to the old
// index fail with "can't assign requested address" for the rest of the session.
type ifIndex struct {
	name string

	mu  sync.Mutex
	idx int
	at  time.Time
}

const ifIndexTTL = 2 * time.Second

func (i *ifIndex) get() int {
	i.mu.Lock()
	defer i.mu.Unlock()
	if i.idx <= 0 || time.Since(i.at) > ifIndexTTL {
		if iface, err := net.InterfaceByName(i.name); err == nil && iface.Index > 0 {
			i.idx = iface.Index
		}
		i.at = time.Now()
	}
	return i.idx
}

// boundDialer returns a dialer whose sockets are scoped to the interface of the
// original default route (IP_BOUND_IF). While the tunnel is up, every other
// route points at the TUN, so this is what keeps the core's own upstream
// connections (to proxies and to direct destinations) out of its own tunnel.
func boundDialer(ifName string) *net.Dialer {
	d := &net.Dialer{}
	if ifName == "" {
		return d
	}
	ix := &ifIndex{name: ifName}
	if ix.get() <= 0 {
		return d
	}
	d.Control = func(network, address string, c syscall.RawConn) error {
		var serr error
		err := c.Control(func(fd uintptr) {
			serr = unix.SetsockoptInt(int(fd), unix.IPPROTO_IP, unix.IP_BOUND_IF, ix.get())
		})
		if err != nil {
			return err
		}
		return serr
	}
	return d
}
