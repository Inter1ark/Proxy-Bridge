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
	return gw, nil
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

// addSplitRoutes covers the IPv4 space with routes through the tunnel: the /1
// pair, or, when a VPN already holds those, the four /2 routes (and /3 after a
// VPN that fell back to /2 itself). The VPN keeps its routes and stays usable.
func addSplitRoutes(iface string, undo *Undo) error {
	for bits := 1; bits <= maxSplitBits; bits++ {
		var added []netip.Prefix
		rollback := func() {
			for _, p := range added {
				_, _ = run("route", "-n", "delete", "-inet", "-net", p.String())
			}
		}
		exists := false
		var failure error
		for _, p := range splitPrefixes(bits) {
			if _, err := run("route", "-n", "add", "-inet", "-net", p.String(), "-interface", iface); err != nil {
				if strings.Contains(err.Error(), "exists") {
					exists = true
				} else {
					failure = err
				}
				break
			}
			added = append(added, p)
		}
		if failure != nil {
			rollback()
			return failure
		}
		if exists {
			rollback()
			Logf("/%d routes are taken (a VPN?), trying /%d", bits, bits+1)
			continue
		}
		for _, p := range added {
			n := p.String()
			undo.Push(func() error {
				_, err := run("route", "-n", "delete", "-inet", "-net", n)
				return err
			})
		}
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
