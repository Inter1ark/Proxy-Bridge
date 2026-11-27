//go:build darwin

package netcfg

import (
	"fmt"
	"net/netip"
	"strconv"
	"strings"
)

// DefaultGateway reads the current IPv4 default route with "route -n get default".
// When a VPN is up, the internet route is usually not the default route but its
// 0/1 and 128/1 pair (or a default route on its utun); the route that an
// internet address actually takes is probed as well, and when it leaves through
// another interface than the default route, that tunnel is what direct traffic
// and the proxy connections must keep using (the VPN stays in the chain).
func DefaultGateway() (Gateway, error) {
	gw, err := routeGet("default")
	if err != nil {
		return Gateway{}, fmt.Errorf("read default route: %w", err)
	}
	probe, perr := routeGet("8.8.8.8")
	if perr == nil && probe.Interface != "" && probe.Interface != gw.Interface && !strings.HasPrefix(probe.Interface, "lo") {
		Logf("internet route leaves through %s, not the default route's %s: treating it as a VPN", probe.Interface, gw.Interface)
		probe.Tunnel = true
		return probe, nil
	}
	// A VPN that replaced the default route itself: the default route is its utun.
	if IsTunnelName(gw.Interface) {
		gw.Tunnel = true
	}
	return gw, nil
}

// PhysicalGateway finds the default route of the network card under a VPN. macOS keeps it
// as an interface-scoped default route ("default 192.168.0.1 UGScIg en0") while the VPN
// owns the unscoped one. vpnInterface is skipped. ok is false when there is none.
func PhysicalGateway(vpnInterface string) (Gateway, bool) {
	out, err := run("netstat", "-rn", "-f", "inet")
	if err != nil {
		return Gateway{}, false
	}
	return parsePhysicalGateway(out, vpnInterface)
}

// Describe returns a few lines about the VPN that owns the internet route, for the support
// log: the addresses of its interface and the routes that decide where traffic goes (the
// default routes, the 0/1 style pairs and host routes through a gateway, which is how VPN
// clients pin their server).
func Describe(vpnInterface string) []string {
	var out []string
	if cfg, err := runQuiet("ifconfig", vpnInterface); err == nil {
		for _, line := range strings.Split(cfg, "\n") {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "inet ") || strings.HasPrefix(line, "inet6 ") || strings.Contains(line, "mtu ") {
				out = append(out, vpnInterface+": "+line)
			}
		}
	}
	rt, err := runQuiet("netstat", "-rn", "-f", "inet")
	if err != nil {
		return out
	}
	n := 0
	for _, line := range strings.Split(rt, "\n") {
		f := strings.Fields(line)
		if len(f) < 4 || n >= 24 {
			continue
		}
		dest, flags := f[0], f[2]
		wide := dest == "default" || strings.HasSuffix(dest, "/1") || strings.HasSuffix(dest, "/2") || dest == "128.0/1"
		pinned := strings.Contains(flags, "H") && strings.Contains(flags, "G") && strings.Contains(flags, "S")
		if wide || pinned {
			out = append(out, "route: "+strings.Join(f[:4], " "))
			n++
		}
	}
	return out
}

func parsePhysicalGateway(netstat, vpnInterface string) (Gateway, bool) {
	for _, line := range strings.Split(netstat, "\n") {
		f := strings.Fields(line)
		if len(f) < 4 || f[0] != "default" {
			continue
		}
		iface := f[3]
		if iface == vpnInterface || IsTunnelName(iface) || strings.HasPrefix(iface, "lo") {
			continue
		}
		ip, err := netip.ParseAddr(f[1])
		if err != nil || !ip.Is4() {
			continue
		}
		return Gateway{IP: ip, Interface: iface}, true
	}
	return Gateway{}, false
}

// privateRanges are the address ranges that never belong to the internet: the local
// network and the inside of a VPN (its gateway, its DNS, company hosts).
var privateRanges = []string{"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10"}

// KeepPrivateInVPN routes the private ranges to the VPN that owns the internet route, as
// they went before the session. The tunnel routes of the session are more specific than the
// VPN's own pair, so without this everything addressed to the inside of the VPN lands in
// the session's tunnel, which carries TCP and UDP only: the ping a VPN client sends to its
// gateway every few seconds to see that the tunnel is alive gets no answer, and the client
// restarts the tunnel again and again. A proxy could not reach those addresses anyway.
// Ranges that already have such a route (the VPN added it itself) are left alone.
func KeepPrivateInVPN(gw Gateway, undo *Undo) {
	for _, p := range privateRanges {
		if addPrivateRoute(p, gw) && undo != nil {
			n := p
			undo.Push(func() error {
				_, err := run("route", append([]string{"-n", "delete", "-inet", "-net", n}, viaArgs(gw)...)...)
				if err != nil && strings.Contains(err.Error(), "not in table") {
					return nil // the VPN went away and took the route with it
				}
				return err
			})
		}
	}
}

func addPrivateRoute(prefix string, gw Gateway) bool {
	_, err := run("route", append([]string{"-n", "add", "-inet", "-net", prefix}, viaArgs(gw)...)...)
	return err == nil
}

// RepairRoutes puts back the routes that live on the original interface after it was
// destroyed and created again (a VPN client that reconnected): the scoped default route,
// the host routes of the proxies and, with private set, the private ranges. Routes that are
// still there are left alone. The cleanup steps registered when they were first added stay
// valid.
func RepairRoutes(gw Gateway, hosts []netip.Addr, private bool) error {
	if private {
		for _, p := range privateRanges {
			addPrivateRoute(p, gw)
		}
	}
	return repairRoutes(gw, hosts)
}

func repairRoutes(gw Gateway, hosts []netip.Addr) error {
	var first error
	note := func(err error) {
		if err != nil && !strings.Contains(err.Error(), "exists") && first == nil {
			first = err
		}
	}
	if gw.Interface != "" {
		args := append([]string{"-n", "add", "-inet", "default"}, viaArgs(gw)...)
		_, err := run("route", append(args, "-ifscope", gw.Interface)...)
		note(err)
	}
	for _, ip := range hosts {
		_, err := run("route", append([]string{"-n", "add", "-inet", "-host", ip.String()}, viaArgs(gw)...)...)
		note(err)
	}
	return first
}

// routeGet parses "route -n get -inet <dest>" into the gateway and interface.
func routeGet(dest string) (Gateway, error) {
	out, err := run("route", "-n", "get", "-inet", dest)
	if err != nil {
		return Gateway{}, err
	}
	var gw Gateway
	for _, line := range strings.Split(out, "\n") {
		k, v, ok := strings.Cut(strings.TrimSpace(line), ":")
		if !ok {
			continue
		}
		v = strings.TrimSpace(v)
		switch strings.TrimSpace(k) {
		case "gateway":
			if ip, err := netip.ParseAddr(v); err == nil {
				gw.IP = ip
			}
		case "interface":
			gw.Interface = v
		}
	}
	if !gw.IP.IsValid() && gw.Interface == "" {
		return Gateway{}, fmt.Errorf("no route to %s found", dest)
	}
	return gw, nil
}

// ConfigureInterface assigns the point-to-point address and brings the utun up.
func ConfigureInterface(cfg Config) error {
	args := []string{cfg.Interface, "inet", cfg.Addr.Addr().String(), cfg.Peer.String(),
		"netmask", maskString(cfg.Addr.Bits())}
	if cfg.MTU > 0 {
		args = append(args, "mtu", strconv.Itoa(cfg.MTU))
	}
	args = append(args, "up")
	if _, err := run("ifconfig", args...); err != nil {
		return err
	}
	return nil
}

func maskString(bits int) string {
	if bits < 0 || bits > 32 {
		bits = 32
	}
	m := ^uint32(0) << (32 - bits)
	if bits == 0 {
		m = 0
	}
	return fmt.Sprintf("%d.%d.%d.%d", byte(m>>24), byte(m>>16), byte(m>>8), byte(m))
}

func viaArgs(gw Gateway) []string {
	if gw.IP.IsValid() {
		return []string{gw.IP.String()}
	}
	return []string{"-interface", gw.Interface}
}

// AddHostRoute pins ip to the original gateway so it bypasses the tunnel.
func AddHostRoute(ip netip.Addr, gw Gateway, undo *Undo) error {
	args := append([]string{"-n", "add", "-inet", "-host", ip.String()}, viaArgs(gw)...)
	if _, err := run("route", args...); err != nil {
		return err
	}
	undo.Push(func() error {
		_, err := run("route", "-n", "delete", "-inet", "-host", ip.String())
		return err
	})
	return nil
}

// RouteAllViaTun points all IPv4 traffic at the tunnel according to cfg.Mode.
func RouteAllViaTun(cfg Config, original Gateway, undo *Undo) error {
	switch cfg.Mode {
	case RouteReplace:
		if _, err := run("route", "-n", "delete", "-inet", "default"); err != nil {
			return err
		}
		undo.Push(func() error {
			_, _ = run("route", "-n", "delete", "-inet", "default")
			args := append([]string{"-n", "add", "-inet", "default"}, viaArgs(original)...)
			_, err := run("route", args...)
			return err
		})
		if _, err := run("route", "-n", "add", "-inet", "default", cfg.Peer.String()); err != nil {
			return err
		}
		return nil
	default:
		return addSplitRoutes(cfg.Interface, undo)
	}
}

// routeTaken reports whether a route with exactly this destination and mask is
// already in the table (a VPN's 0/1 and 128/1, for instance). "route get" answers
// with the most specific covering route, so a shorter prefix shows up as a
// different mask. The check matters because "route add" of a prefix that another
// interface already holds reports success on macOS without installing anything,
// and deleting it afterwards would remove the VPN's route instead.
func routeTaken(p netip.Prefix) bool {
	out, err := run("route", "-n", "get", "-inet", "-net", p.String())
	if err != nil {
		return false
	}
	var dest, mask string
	for _, line := range strings.Split(out, "\n") {
		k, v, ok := strings.Cut(strings.TrimSpace(line), ":")
		if !ok {
			continue
		}
		switch strings.TrimSpace(k) {
		case "destination":
			dest = strings.TrimSpace(v)
		case "mask":
			mask = strings.TrimSpace(v)
		}
	}
	addr := p.Masked().Addr().String()
	if dest == "default" {
		dest = "0.0.0.0"
	}
	return dest == addr && mask == maskString(p.Bits())
}

// addSplitRoutes covers the IPv4 space with routes through the tunnel: the /1
// pair, or, when a VPN already holds those, the four /2 routes (and /3 after a
// VPN that fell back to /2 itself). The VPN keeps its routes and stays usable.
func addSplitRoutes(iface string, undo *Undo) error {
	for bits := 1; bits <= maxSplitBits; bits++ {
		prefixes := splitPrefixes(bits)
		taken := false
		for _, p := range prefixes {
			if routeTaken(p) {
				Logf("route %s is already taken (a VPN?)", p)
				taken = true
				break
			}
		}
		if taken {
			continue
		}
		var added []netip.Prefix
		rollback := func() {
			for _, p := range added {
				_, _ = run("route", "-n", "delete", "-inet", "-net", p.String(), "-interface", iface)
			}
		}
		for _, p := range prefixes {
			if _, err := run("route", "-n", "add", "-inet", "-net", p.String(), "-interface", iface); err != nil {
				rollback()
				if strings.Contains(err.Error(), "exists") {
					Logf("route %s appeared meanwhile, trying /%d", p, bits+1)
					taken = true
					break
				}
				return err
			}
			added = append(added, p)
		}
		if taken {
			continue
		}
		for _, p := range added {
			n := p.String()
			undo.Push(func() error {
				_, err := run("route", "-n", "delete", "-inet", "-net", n, "-interface", iface)
				return err
			})
		}
		lastSplitBits = bits
		if bits > 1 {
			Logf("tunnel routes installed as /%d next to the existing /%d pair", bits, bits-1)
		}
		return nil
	}
	return fmt.Errorf("could not install tunnel routes: /1 through /%d are all taken", maxSplitBits)
}

// AddScopedDefault installs a default route scoped to the original interface
// ("route add default <gw> -ifscope en0", or "-interface utun4 -ifscope utun4"
// for a VPN tunnel without a next hop). Sockets bound to that interface with
// IP_BOUND_IF use scoped routes first; without one, the lookup picks the 0/1
// and 128/1 tunnel routes and then rejects them for the wrong interface
// ("network is unreachable"). A route that already exists is left alone.
// tunIPv6 is the IPv6 address of our tunnel interface (a unique local address: it is only
// the source of the packets the system hands to the tunnel, it never leaves the Mac).
const tunIPv6 = "fd70:6272:6467::2/128"

// ipv6Routes cover the global unicast space 2000::/3 with two /4 routes: narrower than the
// ::/1 pair or the default route a VPN installs, and clear of link-local, unique local and
// multicast addresses, which stay where they are.
var ipv6Routes = []string{"2000::/4", "3000::/4"}

// RouteIPv6ViaTun sends IPv6 traffic to the internet into the tunnel. Without it a program
// with a proxy rule reaches dual-stack sites over IPv6 around the tunnel, and the site sees
// the real (or the VPN's) IPv6 address. Nothing is changed when the Mac has no IPv6 route to
// the internet: then no program can use IPv6 either. It returns the interface IPv6 traffic
// left through before, "" when there is none.
func RouteIPv6ViaTun(iface string, undo *Undo) (string, error) {
	out, err := runQuiet("route", "-n", "get", "-inet6", "2606:4700:4700::1111")
	if err != nil {
		return "", nil
	}
	via, gateway := "", ""
	for _, l := range strings.Split(out, "\n") {
		k, v, ok := strings.Cut(strings.TrimSpace(l), ":")
		if !ok {
			continue
		}
		switch strings.TrimSpace(k) {
		case "interface":
			via = strings.TrimSpace(v)
		case "gateway":
			gateway = strings.TrimSpace(v)
		}
	}
	if via == "" || via == iface {
		return "", nil
	}
	if _, err := run("ifconfig", iface, "inet6", tunIPv6, "alias"); err != nil {
		return via, err
	}
	// sockets bound to the original interface (direct connections) need a scoped IPv6 default
	// there, or their lookup ends in our routes
	scoped := []string{"-n", "add", "-inet6", "default"}
	if gateway == "" || IsTunnelName(via) {
		scoped = append(scoped, "-interface", via)
	} else {
		scoped = append(scoped, gateway)
	}
	scoped = append(scoped, "-ifscope", via)
	if _, err := run("route", scoped...); err == nil {
		undo.Push(func() error {
			_, err := run("route", "-n", "delete", "-inet6", "default", "-ifscope", via)
			return err
		})
	}
	for _, p := range ipv6Routes {
		if _, err := run("route", "-n", "add", "-inet6", "-net", p, "-interface", iface); err != nil {
			return via, err
		}
		p := p
		undo.Push(func() error {
			_, err := run("route", "-n", "delete", "-inet6", "-net", p, "-interface", iface)
			return err
		})
	}
	return via, nil
}

func AddScopedDefault(gw Gateway, undo *Undo) error {
	if gw.Interface == "" {
		return nil
	}
	args := append([]string{"-n", "add", "-inet", "default"}, viaArgs(gw)...)
	args = append(args, "-ifscope", gw.Interface)
	_, err := run("route", args...)
	if err != nil {
		if strings.Contains(err.Error(), "exists") {
			return nil
		}
		return err
	}
	undo.Push(func() error {
		_, err := run("route", "-n", "delete", "-inet", "default", "-ifscope", gw.Interface)
		return err
	})
	return nil
}
