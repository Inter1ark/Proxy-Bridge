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
	got := parseDNSServers("37.128.241.21\n1.1.1.1\nfe80::1\n")
	if want := []string{"37.128.241.21", "1.1.1.1", "fe80::1"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v, want %v", got, want)
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
