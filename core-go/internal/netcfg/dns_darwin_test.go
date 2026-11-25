//go:build darwin

package netcfg

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestParseDNSServers(t *testing.T) {
	if got := parseDNSServers("There aren't any DNS Servers set on Wi-Fi.\n"); len(got) != 0 {
		t.Fatalf("no servers expected, got %v", got)
	}
	got := parseDNSServers("203.0.113.53\n1.1.1.1\nfe80::1\n")
	if want := []string{"203.0.113.53", "1.1.1.1", "fe80::1"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
}

func TestWithoutServer(t *testing.T) {
	got := withoutServer([]string{"10.255.0.1", "1.1.1.1", "10.255.0.1"}, "10.255.0.1")
	if len(got) != 1 || got[0] != "1.1.1.1" {
		t.Fatalf("got %v", got)
	}
	if got := withoutServer([]string{"10.255.0.1"}, "10.255.0.1"); len(got) != 0 {
		t.Fatalf("got %v, want none (the service goes back to automatic)", got)
	}
	if got := withoutServer([]string{"8.8.8.8"}, "10.255.0.1"); len(got) != 1 {
		t.Fatalf("got %v", got)
	}
}

func TestBackupRoundTrip(t *testing.T) {
	old := DNSBackupPath
	DNSBackupPath = filepath.Join(t.TempDir(), "sub", "dns-backup.json")
	defer func() { DNSBackupPath = old }()
	b := dnsBackup{Services: map[string][]string{"Wi-Fi": {"192.168.1.1"}, "Ethernet": nil}, Set: "10.255.0.1"}
	if err := writeBackup(b); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(DNSBackupPath); err != nil {
		t.Fatalf("backup not written: %v", err)
	}
	if keys := sortedKeys(b.Services); !reflect.DeepEqual(keys, []string{"Ethernet", "Wi-Fi"}) {
		t.Fatalf("keys %v", keys)
	}
}

func TestParsePhysicalGateway(t *testing.T) {
	// a VPN that replaced the default route: the network card keeps a scoped one
	vpnDefault := `Routing tables

Internet:
Destination        Gateway            Flags               Netif Expire
default            link#25            UCSg                utun6
default            192.168.0.1        UGScIg                en0
10.255.0/24        10.255.0.2         UGSc                utun7
127                127.0.0.1          UCS                   lo0
`
	gw, ok := parsePhysicalGateway(vpnDefault, "utun6")
	if !ok || gw.Interface != "en0" || gw.IP.String() != "192.168.0.1" {
		t.Fatalf("got %+v ok=%v, want en0 via 192.168.0.1", gw, ok)
	}
	// a VPN with the 0/1 and 128/1 pair: the default route itself is the network card
	split := `Destination        Gateway            Flags               Netif Expire
0/1                10.8.0.1           UGScg               utun4
default            10.0.0.1           UGScg                 en1
128.0/1            10.8.0.1           UGScg               utun4
`
	gw, ok = parsePhysicalGateway(split, "utun4")
	if !ok || gw.Interface != "en1" || gw.IP.String() != "10.0.0.1" {
		t.Fatalf("got %+v ok=%v, want en1 via 10.0.0.1", gw, ok)
	}
	// only tunnels: nothing to fall back to
	if gw, ok := parsePhysicalGateway("default link#25 UCSg utun6\ndefault link#30 UCSIg utun9\n", "utun6"); ok {
		t.Fatalf("got %+v, want none", gw)
	}
}

func TestIsTunnelName(t *testing.T) {
	for name, want := range map[string]bool{"utun6": true, "ipsec0": true, "ppp0": true, "en0": false, "bridge100": false, "": false} {
		if IsTunnelName(name) != want {
			t.Errorf("IsTunnelName(%q) = %v, want %v", name, !want, want)
		}
	}
}
