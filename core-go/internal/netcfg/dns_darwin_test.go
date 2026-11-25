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
