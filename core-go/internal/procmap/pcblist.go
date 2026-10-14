// Package procmap finds out which process owns a TCP connection, so pbcore
// can apply per-program rules to the flows that arrive through the TUN.
//
// On macOS the kernel exports every TCP control block together with the pid
// that last used its socket through the sysctl "net.inet.tcp.pcblist_n"
// (this is what "netstat -v" shows in its pid column). The parser below is
// platform independent so it can be unit tested anywhere; the sysctl call,
// the pid to executable lookup and the lsof fallback live in procmap_darwin.go.
package procmap

import (
	"encoding/binary"
	"net/netip"
)

// Record kinds inside the pcblist_n stream (bsd/sys/socketvar.h).
const (
	xsoSocket = 0x001
	xsoInpcb  = 0x010
	xsoTcpcb  = 0x020
)

// Field offsets of struct xinpcb_n and struct xsocket_n (64-bit layout).
const (
	inpFport  = 16
	inpLport  = 18
	inpVflag  = 48
	inpFaddr  = 52 // 16-byte union; IPv4 lives in the last 4 bytes
	inpLaddr  = 68
	inpMinLen = 84

	soLastPid = 72
	soMinLen  = 76

	inpIPv4 = 0x1
)

// Conn is one TCP control block with its owner.
type Conn struct {
	Local  netip.AddrPort
	Remote netip.AddrPort
	PID    int
}

// ParsePcblist decodes a net.inet.tcp.pcblist_n buffer into connections.
// Entries whose owner is unknown (pid 0) or that are not IPv4 are skipped.
func ParsePcblist(b []byte) []Conn {
	var out []Conn
	// The stream starts with an xinpgen header, then a sequence of (len, kind)
	// prefixed records: every inpcb is followed by its socket record.
	var cur *Conn
	for off := 0; off+8 <= len(b); {
		l := int(binary.LittleEndian.Uint32(b[off:]))
		kind := binary.LittleEndian.Uint32(b[off+4:])
		if l < 8 || off+l > len(b) {
			break
		}
		rec := b[off : off+l]
		switch kind {
		case xsoInpcb:
			cur = nil
			if l < inpMinLen || rec[inpVflag]&inpIPv4 == 0 {
				break
			}
			lport := binary.BigEndian.Uint16(rec[inpLport:])
			fport := binary.BigEndian.Uint16(rec[inpFport:])
			var la, fa [4]byte
			copy(la[:], rec[inpLaddr+12:inpLaddr+16])
			copy(fa[:], rec[inpFaddr+12:inpFaddr+16])
			cur = &Conn{
				Local:  netip.AddrPortFrom(netip.AddrFrom4(la), lport),
				Remote: netip.AddrPortFrom(netip.AddrFrom4(fa), fport),
			}
		case xsoSocket:
			if cur != nil && l >= soMinLen {
				cur.PID = int(int32(binary.LittleEndian.Uint32(rec[soLastPid:])))
				if cur.PID > 0 {
					out = append(out, *cur)
				}
				cur = nil
			}
		}
		off += l
	}
	return out
}

// Find returns the pid owning the connection with these endpoints. The remote
// side is compared only when it is known (port != 0); a connection matched by
// local endpoint alone is returned when nothing closer exists.
func Find(conns []Conn, local, remote netip.AddrPort) (int, bool) {
	fallback := 0
	for _, c := range conns {
		if c.Local.Port() != local.Port() {
			continue
		}
		if local.Addr().IsValid() && !local.Addr().IsUnspecified() && c.Local.Addr() != local.Addr() && !c.Local.Addr().IsUnspecified() {
			continue
		}
		if remote.Port() != 0 {
			if c.Remote.Port() == remote.Port() && c.Remote.Addr() == remote.Addr() {
				return c.PID, true
			}
			if fallback == 0 {
				fallback = c.PID
			}
			continue
		}
		return c.PID, true
	}
	return fallback, fallback != 0
}
