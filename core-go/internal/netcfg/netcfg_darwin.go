//go:build darwin

package netcfg

import (
	"fmt"
	"net/netip"
	"strconv"
	"strings"
)

// DefaultGateway reads the current IPv4 default route with "route -n get default".
func DefaultGateway() (Gateway, error) {
	out, err := run("route", "-n", "get", "-inet", "default")
	if err != nil {
		return Gateway{}, fmt.Errorf("read default route: %w", err)
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
		return Gateway{}, fmt.Errorf("no default route found")
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
		for _, net := range []string{"0.0.0.0/1", "128.0.0.0/1"} {
			n := net
			if _, err := run("route", "-n", "add", "-inet", "-net", n, "-interface", cfg.Interface); err != nil {
				return err
			}
			undo.Push(func() error {
				_, err := run("route", "-n", "delete", "-inet", "-net", n)
				return err
			})
		}
		return nil
	}
}
