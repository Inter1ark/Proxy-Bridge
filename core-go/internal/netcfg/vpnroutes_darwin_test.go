//go:build darwin

package netcfg

import (
	"net/netip"
	"os"
	"os/exec"
	"strings"
	"testing"

	"golang.zx2c4.com/wireguard/tun"
)

func TestHalves(t *testing.T) {
	h := halves(netip.MustParsePrefix("203.0.112.0/22"))
	if len(h) != 2 || h[0].String() != "203.0.112.0/23" || h[1].String() != "203.0.114.0/23" {
		t.Fatalf("got %v", h)
	}
	h = halves(netip.MustParsePrefix("128.0.0.0/2"))
	if h[0].String() != "128.0.0.0/3" || h[1].String() != "160.0.0.0/3" {
		t.Fatalf("got %v", h)
	}
}

func TestPrefixSet(t *testing.T) {
	s := NewPrefixSet([]netip.Prefix{netip.MustParsePrefix("5.0.0.0/8"), netip.MustParsePrefix("93.184.216.0/24")})
	for addr, want := range map[string]bool{"5.6.7.8": true, "93.184.216.34": true, "93.184.217.1": false, "8.8.8.8": false} {
		if s.Contains(netip.MustParseAddr(addr)) != want {
			t.Errorf("Contains(%s) = %v", addr, !want)
		}
	}
}

func TestSpecialNetworksStayWithTheVPN(t *testing.T) {
	for _, p := range []string{"10.66.0.0/16", "192.168.3.0/24", "198.18.0.0/16", "100.64.0.0/10", "224.0.0.0/4"} {
		if !special(netip.MustParsePrefix(p)) {
			t.Errorf("%s must be left to the VPN", p)
		}
	}
	if special(netip.MustParsePrefix("5.0.0.0/8")) {
		t.Error("a public network must be covered")
	}
}

// TestCoverVPNNetsLive needs root (PB_ROOT_TESTS=1): two utun interfaces, a "VPN" with split
// routes over documentation ranges, then our narrower routes must win and be removed again.
func TestCoverVPNNetsLive(t *testing.T) {
	if os.Getenv("PB_ROOT_TESTS") != "1" {
		t.Skip("set PB_ROOT_TESTS=1 and run as root")
	}
	vpn, err := tun.CreateTUN("utun", 1400)
	if err != nil {
		t.Fatal(err)
	}
	defer vpn.Close()
	ours, err := tun.CreateTUN("utun", 1400)
	if err != nil {
		t.Fatal(err)
	}
	defer ours.Close()
	vName, _ := vpn.Name()
	oName, _ := ours.Name()
	mustRun(t, "ifconfig", vName, "inet", "10.123.0.2", "10.123.0.2", "netmask", "255.255.255.255", "up")
	mustRun(t, "ifconfig", oName, "inet", "10.124.0.2", "10.124.0.1", "netmask", "255.255.255.0", "up")
	for _, p := range []string{"203.0.113.0/24", "198.51.100.0/24", "192.0.2.0/25", "192.0.2.0/26"} {
		mustRun(t, "route", "-n", "add", "-inet", "-net", p, "-interface", vName)
	}
	nets, all, err := VPNNets(vName, 2)
	if err != nil || all {
		t.Fatalf("nets %v all %v err %v", nets, all, err)
	}
	if len(nets) != 4 {
		t.Fatalf("want the 4 VPN networks, got %v", nets)
	}
	var undo Undo
	n, err := CoverVPNNets(nets, oName, &undo)
	if err != nil {
		t.Fatalf("cover: %v (added %d)", err, n)
	}
	// 4 networks, 2 halves each, but one half of 192.0.2.0/25 is the VPN's own /26: 7 routes
	if n != 7 {
		t.Fatalf("added %d routes, want 7", n)
	}
	for _, addr := range []string{"203.0.113.9", "203.0.113.200", "198.51.100.7", "192.0.2.10", "192.0.2.100"} {
		if got := routeIface(t, addr); got != oName {
			t.Errorf("%s goes to %s, want %s", addr, got, oName)
		}
	}
	if err := undo.Run(); err != nil {
		t.Fatalf("cleanup: %v", err)
	}
	if got := routeIface(t, "203.0.113.9"); got != vName {
		t.Errorf("after cleanup 203.0.113.9 goes to %s, want the VPN %s", got, vName)
	}
}

func mustRun(t *testing.T, name string, args ...string) {
	t.Helper()
	if out, err := exec.Command(name, args...).CombinedOutput(); err != nil {
		t.Fatalf("%s %v: %v %s", name, args, err, out)
	}
}

func routeIface(t *testing.T, addr string) string {
	out, _ := exec.Command("route", "-n", "get", addr).CombinedOutput()
	for _, l := range strings.Split(string(out), "\n") {
		if k, v, ok := strings.Cut(strings.TrimSpace(l), ":"); ok && strings.TrimSpace(k) == "interface" {
			return strings.TrimSpace(v)
		}
	}
	return ""
}
