//go:build darwin

package netcfg

import (
	"errors"
	"fmt"
	"net"
	"net/netip"
	"syscall"

	"golang.org/x/net/route"
	"golang.org/x/sys/unix"
)

// A VPN with split tunnelling (for example "Russian networks outside the tunnel") does not
// hold the whole internet with a default route or a 0/1 pair: it installs a long list of
// narrower network routes. Each of them is more specific than the tunnel routes of the
// session, so traffic to those networks goes straight into the VPN and never reaches the
// rules. VPNNets reads those routes; CoverVPNNets installs, for each of them, its two halves
// on our tunnel, which are more specific again.

// maxVPNNets bounds the routes taken over (two of ours per VPN route).
const maxVPNNets = 20000

// VPNNets returns the IPv4 network routes that point at iface and are narrower than /minBits-1,
// without the VPN's own network (the one holding the interface address) and without private
// and special ranges. all is true when the VPN holds the whole IPv4 space itself (a default
// route or a 0/1 pair on iface).
func VPNNets(iface string, minBits int) (nets []netip.Prefix, all bool, err error) {
	ifi, err := net.InterfaceByName(iface)
	if err != nil {
		return nil, false, err
	}
	own := interfaceAddrs(ifi)
	rib, err := route.FetchRIB(syscall.AF_INET, route.RIBTypeRoute, 0)
	if err != nil {
		return nil, false, err
	}
	msgs, err := route.ParseRIB(route.RIBTypeRoute, rib)
	if err != nil {
		return nil, false, err
	}
	seen := map[netip.Prefix]bool{}
	for _, m := range msgs {
		rm, ok := m.(*route.RouteMessage)
		if !ok || rm.Index != ifi.Index || rm.Flags&unix.RTF_UP == 0 {
			continue
		}
		if rm.Flags&(unix.RTF_HOST|unix.RTF_IFSCOPE|unix.RTF_WASCLONED) != 0 {
			continue
		}
		p, ok := ipv4Prefix(rm.Addrs)
		if !ok {
			continue
		}
		if p.Bits() <= 1 {
			all = true
			continue
		}
		if p.Bits() < minBits || p.Bits() > 30 || seen[p] || special(p) || containsAny(p, own) {
			continue
		}
		seen[p] = true
		nets = append(nets, p)
	}
	if len(nets) > maxVPNNets {
		return nil, all, fmt.Errorf("%d routes on %s, more than %d", len(nets), iface, maxVPNNets)
	}
	return nets, all, nil
}

func ipv4Prefix(addrs []route.Addr) (netip.Prefix, bool) {
	if len(addrs) <= unix.RTAX_NETMASK {
		return netip.Prefix{}, false
	}
	dst, ok := addrs[unix.RTAX_DST].(*route.Inet4Addr)
	if !ok {
		return netip.Prefix{}, false
	}
	bits := 0
	if mask, ok := addrs[unix.RTAX_NETMASK].(*route.Inet4Addr); ok {
		bits, _ = net.IPMask(mask.IP[:]).Size()
	} else {
		return netip.Prefix{}, false
	}
	p, err := netip.AddrFrom4(dst.IP).Prefix(bits)
	return p, err == nil
}

func interfaceAddrs(ifi *net.Interface) []netip.Addr {
	var out []netip.Addr
	addrs, _ := ifi.Addrs()
	for _, a := range addrs {
		if n, ok := a.(*net.IPNet); ok {
			if ip, ok := netip.AddrFromSlice(n.IP); ok && ip.Unmap().Is4() {
				out = append(out, ip.Unmap())
			}
		}
	}
	return out
}

func containsAny(p netip.Prefix, addrs []netip.Addr) bool {
	for _, a := range addrs {
		if p.Contains(a) {
			return true
		}
	}
	return false
}

var specialNets = []netip.Prefix{
	netip.MustParsePrefix("10.0.0.0/8"), netip.MustParsePrefix("172.16.0.0/12"),
	netip.MustParsePrefix("192.168.0.0/16"), netip.MustParsePrefix("100.64.0.0/10"),
	netip.MustParsePrefix("127.0.0.0/8"), netip.MustParsePrefix("169.254.0.0/16"),
	netip.MustParsePrefix("224.0.0.0/3"), netip.MustParsePrefix("198.18.0.0/15"),
}

// special: private, loopback, link-local, multicast and the benchmark range VPN clients use
// for their own fake addresses: those stay where the VPN put them.
func special(p netip.Prefix) bool {
	for _, s := range specialNets {
		if s.Overlaps(p) {
			return true
		}
	}
	return false
}

// CoverVPNNets adds, for every VPN network, its two halves on our tunnel interface (a half
// that is itself one of the VPN's networks is left out: its own halves cover it). The cleanup
// is registered on undo. It returns how many routes were added.
func CoverVPNNets(nets []netip.Prefix, tunIface string, undo *Undo) (int, error) {
	if len(nets) == 0 {
		return 0, nil
	}
	ifi, err := net.InterfaceByName(tunIface)
	if err != nil {
		return 0, err
	}
	vpn := make(map[netip.Prefix]bool, len(nets))
	for _, p := range nets {
		vpn[p] = true
	}
	fd, err := unix.Socket(unix.AF_ROUTE, unix.SOCK_RAW, unix.AF_UNSPEC)
	if err != nil {
		return 0, err
	}
	defer unix.Close(fd)
	var added []netip.Prefix
	var first error
	seq := 1
	for _, p := range nets {
		for _, h := range halves(p) {
			if vpn[h] {
				continue
			}
			seq++
			err := routeMsg(fd, unix.RTM_ADD, h, ifi.Index, seq)
			if err != nil {
				if errors.Is(err, unix.EEXIST) {
					continue
				}
				if first == nil {
					first = fmt.Errorf("add %s: %w", h, err)
				}
				continue
			}
			added = append(added, h)
		}
	}
	if undo != nil && len(added) > 0 {
		undo.Push(func() error { return RemoveRoutes(added, tunIface) })
	}
	return len(added), first
}

// RemoveRoutes deletes network routes on iface added by CoverVPNNets.
func RemoveRoutes(routes []netip.Prefix, iface string) error {
	fd, err := unix.Socket(unix.AF_ROUTE, unix.SOCK_RAW, unix.AF_UNSPEC)
	if err != nil {
		return err
	}
	defer unix.Close(fd)
	idx := 0
	if ifi, err := net.InterfaceByName(iface); err == nil {
		idx = ifi.Index
	}
	var first error
	for i, p := range routes {
		if err := routeMsg(fd, unix.RTM_DELETE, p, idx, i+1); err != nil && !errors.Is(err, unix.ESRCH) && first == nil {
			first = fmt.Errorf("delete %s: %w", p, err)
		}
	}
	Logf("cleanup: %d routes over the VPN's networks removed", len(routes))
	return first
}

func halves(p netip.Prefix) []netip.Prefix {
	if p.Bits() >= 32 {
		return nil
	}
	lo := netip.PrefixFrom(p.Masked().Addr(), p.Bits()+1)
	a := p.Masked().Addr().As4()
	bit := 31 - p.Bits()
	v := uint32(a[0])<<24 | uint32(a[1])<<16 | uint32(a[2])<<8 | uint32(a[3])
	v |= 1 << bit
	hi := netip.PrefixFrom(netip.AddrFrom4([4]byte{byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}), p.Bits()+1)
	return []netip.Prefix{lo, hi}
}

func routeMsg(fd, typ int, p netip.Prefix, ifIndex, seq int) error {
	m := net.CIDRMask(p.Bits(), 32)
	msg := &route.RouteMessage{
		Version: unix.RTM_VERSION,
		Type:    typ,
		Flags:   unix.RTF_UP | unix.RTF_STATIC,
		Seq:     seq,
		Addrs: []route.Addr{
			unix.RTAX_DST:     &route.Inet4Addr{IP: p.Masked().Addr().As4()},
			unix.RTAX_GATEWAY: &route.LinkAddr{Index: ifIndex},
			unix.RTAX_NETMASK: &route.Inet4Addr{IP: [4]byte{m[0], m[1], m[2], m[3]}},
		},
	}
	b, err := msg.Marshal()
	if err != nil {
		return err
	}
	_, err = unix.Write(fd, b)
	return err
}

// PrefixSet answers whether an address falls into one of a set of networks.
type PrefixSet struct {
	byBits [33]map[netip.Addr]bool
	n      int
}

func NewPrefixSet(ps []netip.Prefix) *PrefixSet {
	s := &PrefixSet{}
	for _, p := range ps {
		b := p.Bits()
		if s.byBits[b] == nil {
			s.byBits[b] = map[netip.Addr]bool{}
		}
		s.byBits[b][p.Masked().Addr()] = true
		s.n++
	}
	return s
}

func (s *PrefixSet) Len() int {
	if s == nil {
		return 0
	}
	return s.n
}

func (s *PrefixSet) Contains(a netip.Addr) bool {
	if s == nil || !a.Is4() {
		return false
	}
	for b := 32; b >= 0; b-- {
		m := s.byBits[b]
		if m == nil {
			continue
		}
		if p, err := a.Prefix(b); err == nil && m[p.Addr()] {
			return true
		}
	}
	return false
}
