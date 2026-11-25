//go:build darwin

package netcfg

import (
	"encoding/json"
	"fmt"
	"net/netip"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// macOS resolves names in mDNSResponder, which sends the queries to the DNS servers of the
// network service (Wi-Fi, Ethernet) and reaches them outside the tunnel routes. Pointing the
// services at an address inside the tunnel is the only way the queries reach the engine
// (which then answers them through the proxy). The same approach as wg-quick on macOS.

// DNSBackupPath keeps the DNS servers every network service had before the tunnel changed
// them. It is written before anything is changed, so a helper that dies with the tunnel up
// puts the settings back on its next start (RestoreSystemDNS).
var DNSBackupPath = "/Library/Application Support/ProxyBridge/dns-backup.json"

type dnsBackup struct {
	Services map[string][]string `json:"services"` // service name -> servers ([] = automatic)
	Set      string              `json:"set"`      // the address the tunnel put in place
}

// networkServices lists the enabled network services by name.
func networkServices() ([]string, error) {
	out, err := run("networksetup", "-listallnetworkservices")
	if err != nil {
		return nil, err
	}
	var svcs []string
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "*") || strings.Contains(line, "asterisk") {
			continue
		}
		svcs = append(svcs, line)
	}
	return svcs, nil
}

// parseDNSServers reads "networksetup -getdnsservers" output: one address per line, or a
// sentence saying there are none (the servers then come from DHCP).
func parseDNSServers(out string) []string {
	var servers []string
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if _, err := netip.ParseAddr(line); err == nil {
			servers = append(servers, line)
		}
	}
	return servers
}

func setServiceDNS(svc string, servers []string) error {
	args := []string{"-setdnsservers", svc}
	if len(servers) == 0 {
		args = append(args, "Empty")
	} else {
		args = append(args, servers...)
	}
	_, err := run("networksetup", args...)
	return err
}

func flushDNSCache() {
	_, _ = run("dscacheutil", "-flushcache")
	_, _ = run("killall", "-HUP", "mDNSResponder")
}

// SetSystemDNS points every enabled network service at ip for the life of the tunnel and
// registers the restore step on undo. Servers equal to ip (left by an earlier crash that the
// backup somehow missed) are not saved as the original ones.
func SetSystemDNS(ip netip.Addr, undo *Undo) error {
	if err := RestoreSystemDNS(); err != nil {
		Logf("dns: restoring settings left by an earlier session: %v", err)
	}
	svcs, err := networkServices()
	if err != nil {
		return fmt.Errorf("list network services: %w", err)
	}
	b := dnsBackup{Services: map[string][]string{}, Set: ip.String()}
	for _, svc := range svcs {
		out, err := run("networksetup", "-getdnsservers", svc)
		if err != nil {
			Logf("dns: read servers of %q: %v", svc, err)
			continue
		}
		var keep []string
		for _, s := range parseDNSServers(out) {
			if s != ip.String() {
				keep = append(keep, s)
			}
		}
		b.Services[svc] = keep
	}
	if len(b.Services) == 0 {
		return fmt.Errorf("no network service to configure")
	}
	if err := writeBackup(b); err != nil {
		return fmt.Errorf("save dns backup: %w", err)
	}
	undo.Push(RestoreSystemDNS)
	var failed []string
	for _, svc := range sortedKeys(b.Services) {
		if err := setServiceDNS(svc, []string{ip.String()}); err != nil {
			failed = append(failed, fmt.Sprintf("%s: %v", svc, err))
		}
	}
	flushDNSCache()
	if len(failed) == len(b.Services) {
		return fmt.Errorf("set dns: %s", strings.Join(failed, "; "))
	}
	for _, f := range failed {
		Logf("dns: %s", f)
	}
	return nil
}

// RestoreSystemDNS puts back the servers saved by SetSystemDNS and removes the backup.
// Without a backup it does nothing.
func RestoreSystemDNS() error {
	data, err := os.ReadFile(DNSBackupPath)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	var b dnsBackup
	if err := json.Unmarshal(data, &b); err != nil {
		_ = os.Remove(DNSBackupPath)
		return fmt.Errorf("dns backup is unreadable, removed: %w", err)
	}
	current := map[string]bool{}
	if svcs, err := networkServices(); err == nil {
		for _, s := range svcs {
			current[s] = true
		}
	}
	var failed []string
	for _, svc := range sortedKeys(b.Services) {
		if len(current) > 0 && !current[svc] {
			continue // the service is gone or disabled now
		}
		if err := setServiceDNS(svc, b.Services[svc]); err != nil {
			failed = append(failed, fmt.Sprintf("%s: %v", svc, err))
		}
	}
	flushDNSCache()
	if len(failed) > 0 {
		return fmt.Errorf("restore dns: %s", strings.Join(failed, "; "))
	}
	return os.Remove(DNSBackupPath)
}

// ClearStaleDNS removes ip from the DNS servers of every network service and returns the
// services it fixed. It is meant for the time when no tunnel is up: the address exists only
// inside the tunnel, so a service that still points at it cannot resolve anything. The backup
// does not cover every way to get there: a VPN client that connected while our tunnel was up
// saves our address as "the original DNS" and puts it back when it disconnects, long after our
// own restore.
func ClearStaleDNS(ip netip.Addr) []string {
	svcs, err := networkServices()
	if err != nil {
		return nil
	}
	var fixed []string
	for _, svc := range svcs {
		out, err := run("networksetup", "-getdnsservers", svc)
		if err != nil {
			continue
		}
		servers := parseDNSServers(out)
		keep := withoutServer(servers, ip.String())
		if len(keep) == len(servers) {
			continue
		}
		if err := setServiceDNS(svc, keep); err != nil {
			Logf("dns: clear the tunnel address on %q: %v", svc, err)
			continue
		}
		fixed = append(fixed, svc)
	}
	if len(fixed) > 0 {
		flushDNSCache()
	}
	return fixed
}

// withoutServer returns servers without the entries equal to drop.
func withoutServer(servers []string, drop string) []string {
	var keep []string
	for _, s := range servers {
		if s != drop {
			keep = append(keep, s)
		}
	}
	return keep
}

func writeBackup(b dnsBackup) error {
	if err := os.MkdirAll(filepath.Dir(DNSBackupPath), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(b, "", "  ")
	if err != nil {
		return err
	}
	tmp := DNSBackupPath + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, DNSBackupPath)
}

func sortedKeys(m map[string][]string) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
