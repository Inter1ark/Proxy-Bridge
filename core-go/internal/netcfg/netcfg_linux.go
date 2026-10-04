//go:build linux

package netcfg

import (
	"fmt"
	"net/netip"
	"strconv"
	"strings"
)

// DefaultGateway reads the current IPv4 default route with "ip -4 route show default".
func DefaultGateway() (Gateway, error) {
	out, err := run("ip", "-4", "route", "show", "default")
	if err != nil {
		return Gateway{}, fmt.Errorf("read default route: %w", err)
	}
	var gw Gateway
	for _, line := range strings.Split(out, "\n") {
		f := strings.Fields(line)
		if len(f) == 0 || f[0] != "default" {
			continue
		}
		for i := 1; i+1 < len(f); i++ {
			switch f[i] {
			case "via":
				if ip, err := netip.ParseAddr(f[i+1]); err == nil {
					gw.IP = ip
				}
			case "dev":
				gw.Interface = f[i+1]
			}
		}
		if gw.IP.IsValid() || gw.Interface != "" {
			break
		}
	}
	if !gw.IP.IsValid() && gw.Interface == "" {
		return Gateway{}, fmt.Errorf("no default route found")
	}
	return gw, nil
}

// ConfigureInterface assigns the address and brings the tun up.
func ConfigureInterface(cfg Config) error {
	if _, err := run("ip", "addr", "add", cfg.Addr.String(), "peer", cfg.Peer.String()+"/32", "dev", cfg.Interface); err != nil {
		return err
	}
	args := []string{"link", "set", "dev", cfg.Interface, "up"}
	if cfg.MTU > 0 {
		args = append(args, "mtu", strconv.Itoa(cfg.MTU))
	}
	if _, err := run("ip", args...); err != nil {
		return err
	}
	return nil
}

func viaArgs(gw Gateway) []string {
	var a []string
	if gw.IP.IsValid() {
		a = append(a, "via", gw.IP.String())
	}
	if gw.Interface != "" {
		a = append(a, "dev", gw.Interface)
	}
	return a
}

// AddHostRoute pins ip to the original gateway so it bypasses the tunnel.
func AddHostRoute(ip netip.Addr, gw Gateway, undo *Undo) error {
	args := append([]string{"route", "add", ip.String() + "/32"}, viaArgs(gw)...)
	if _, err := run("ip", args...); err != nil {
		return err
	}
	undo.Push(func() error {
		_, err := run("ip", "route", "del", ip.String()+"/32")
		return err
	})
	return nil
}

// RouteAllViaTun points all IPv4 traffic at the tunnel according to cfg.Mode.
func RouteAllViaTun(cfg Config, original Gateway, undo *Undo) error {
	switch cfg.Mode {
	case RouteReplace:
		if _, err := run("ip", "route", "del", "default"); err != nil {
			return err
		}
		undo.Push(func() error {
			_, _ = run("ip", "route", "del", "default")
			args := append([]string{"route", "add", "default"}, viaArgs(original)...)
			_, err := run("ip", args...)
			return err
		})
		if _, err := run("ip", "route", "add", "default", "dev", cfg.Interface); err != nil {
			return err
		}
		return nil
	default:
		for _, net := range []string{"0.0.0.0/1", "128.0.0.0/1"} {
			n := net
			if _, err := run("ip", "route", "add", n, "dev", cfg.Interface); err != nil {
				return err
			}
			undo.Push(func() error {
				_, err := run("ip", "route", "del", n, "dev", cfg.Interface)
				return err
			})
		}
		return nil
	}
}
