package procmap

import (
	"fmt"
	"net/netip"
	"strconv"
	"strings"
)

// ParseNetstat decodes the output of "netstat -anv -p tcp" (macOS). The pid
// column is located from the header line ("Proto Recv-Q Send-Q Local Address
// Foreign Address (state) rhiwat shiwat pid ..."): "Local Address" and
// "Foreign Address" are two words each in the header but one field each in the rows.
func ParseNetstat(out string) ([]Conn, error) {
	pidIdx := -1
	hasState := false
	var conns []Conn
	for _, line := range strings.Split(out, "\n") {
		f := strings.Fields(line)
		if len(f) == 0 {
			continue
		}
		if f[0] == "Proto" {
			extra := 0
			hasState = false
			for i, w := range f {
				if w == "(state)" {
					hasState = true
				}
				if w == "pid" || w == "process:pid" {
					pidIdx = i - extra
					break
				}
				if w == "Address" {
					extra++
				}
			}
			continue
		}
		isUDP := strings.HasPrefix(f[0], "udp")
		if pidIdx < 0 || (f[0] != "tcp4" && f[0] != "tcp46" && f[0] != "udp4" && f[0] != "udp46") {
			continue
		}
		// UDP rows have no "(state)" column, so everything after the addresses shifts left.
		idx := pidIdx
		if isUDP && hasState {
			idx--
		}
		if len(f) <= idx {
			continue
		}
		local, ok1 := parseNetstatAddr(f[3])
		remote, ok2 := parseNetstatAddr(f[4])
		if !ok1 || !ok2 {
			continue
		}
		// newer macOS prints "name:pid" in one column
		pidStr := f[idx]
		if i := strings.LastIndex(pidStr, ":"); i >= 0 {
			pidStr = pidStr[i+1:]
		}
		pid, err := strconv.Atoi(pidStr)
		if err != nil || pid <= 0 {
			continue
		}
		conns = append(conns, Conn{Local: local, Remote: remote, PID: pid})
	}
	if pidIdx < 0 {
		return nil, fmt.Errorf("netstat: no pid column in the header")
	}
	return conns, nil
}

// parseNetstatAddr turns "10.255.0.2.49479" or "*.*" into an address.
func parseNetstatAddr(s string) (netip.AddrPort, bool) {
	i := strings.LastIndex(s, ".")
	if i <= 0 {
		return netip.AddrPort{}, false
	}
	host, portStr := s[:i], s[i+1:]
	var addr netip.Addr
	if host == "*" {
		addr = netip.IPv4Unspecified()
	} else {
		a, err := netip.ParseAddr(host)
		if err != nil || !a.Is4() {
			return netip.AddrPort{}, false
		}
		addr = a
	}
	port := 0
	if portStr != "*" {
		p, err := strconv.Atoi(portStr)
		if err != nil {
			return netip.AddrPort{}, false
		}
		port = p
	}
	return netip.AddrPortFrom(addr, uint16(port)), true
}
