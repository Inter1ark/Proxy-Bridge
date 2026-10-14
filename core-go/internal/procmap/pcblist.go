// Package procmap finds out which process owns a TCP connection, so pbcore
// can apply per-program rules to the flows that arrive through the TUN.
//
// On macOS the kernel exports every TCP control block together with the pid
// that last used its socket through the sysctl "net.inet.tcp.pcblist_n"
// (this is what "netstat -v" shows in its pid column). The stream is a
// sequence of records, each starting with its length and kind; the field
// offsets inside the records are calibrated at startup against a connection
// the process makes to itself (see procmap_darwin.go), so a different kernel
// layout cannot silently misattribute flows. The parser is platform
// independent so it can be unit tested anywhere.
package procmap

import (
	"encoding/binary"
	"net/netip"
)

// Layout describes where the fields live inside the pcblist records.
type Layout struct {
	InpKind uint32 // record kind of struct xinpcb_n
	SoKind  uint32 // record kind of struct xsocket_n
	Fport   int    // offset of the foreign port (big-endian) in the inpcb record
	Lport   int    // offset of the local port (big-endian)
	Faddr4  int    // offset of the foreign IPv4 address
	Laddr4  int    // offset of the local IPv4 address
	Pid     int    // offset of so_last_pid (little-endian int32) in the socket record
}

// DefaultLayout is the 64-bit layout of XNU's xinpcb_n / xsocket_n
// (bsd/netinet/in_pcb.h, bsd/sys/socketvar.h).
var DefaultLayout = Layout{
	InpKind: 0x010,
	SoKind:  0x001,
	Fport:   16,
	Lport:   18,
	Faddr4:  52 + 12, // 16-byte address union; IPv4 lives in the last 4 bytes
	Laddr4:  68 + 12,
	Pid:     72,
}

// Record is one length-prefixed entry of the stream.
type Record struct {
	Kind uint32
	Data []byte // the whole record including its 8-byte header
}

// Records splits a pcblist_n buffer into records. A record that claims to run
// past the buffer ends the walk.
func Records(b []byte) []Record {
	var out []Record
	for off := 0; off+8 <= len(b); {
		l := int(binary.LittleEndian.Uint32(b[off:]))
		kind := binary.LittleEndian.Uint32(b[off+4:])
		if l < 8 || off+l > len(b) {
			break
		}
		out = append(out, Record{Kind: kind, Data: b[off : off+l]})
		// records are 8-byte aligned in the stream (ROUNDUP64 in netstat's walker)
		off += (l + 7) &^ 7
	}
	return out
}

// Conn is one TCP control block with its owner.
type Conn struct {
	Local  netip.AddrPort
	Remote netip.AddrPort
	PID    int
}

// ParsePcblist decodes a buffer with the default layout.
func ParsePcblist(b []byte) []Conn { return ParsePcblistLayout(b, DefaultLayout) }

// ParsePcblistLayout decodes a buffer: every inpcb record is paired with the
// socket record that follows it. Entries whose owner is unknown (pid 0) are skipped.
func ParsePcblistLayout(b []byte, lay Layout) []Conn {
	var out []Conn
	var cur *Conn
	for _, r := range Records(b) {
		switch r.Kind {
		case lay.InpKind:
			cur = nil
			need := max(lay.Fport+2, lay.Lport+2, lay.Faddr4+4, lay.Laddr4+4)
			if len(r.Data) < need {
				break
			}
			var la, fa [4]byte
			copy(la[:], r.Data[lay.Laddr4:lay.Laddr4+4])
			copy(fa[:], r.Data[lay.Faddr4:lay.Faddr4+4])
			cur = &Conn{
				Local:  netip.AddrPortFrom(netip.AddrFrom4(la), binary.BigEndian.Uint16(r.Data[lay.Lport:])),
				Remote: netip.AddrPortFrom(netip.AddrFrom4(fa), binary.BigEndian.Uint16(r.Data[lay.Fport:])),
			}
		case lay.SoKind:
			if cur != nil && len(r.Data) >= lay.Pid+4 {
				cur.PID = int(int32(binary.LittleEndian.Uint32(r.Data[lay.Pid:])))
				if cur.PID > 0 {
					out = append(out, *cur)
				}
				cur = nil
			}
		}
	}
	return out
}

// Calibrate derives the layout from a buffer that contains a known connection
// (local -> remote, both IPv4) owned by pid. It locates the adjacent
// foreign/local port pair and the two addresses in one record, and the pid in
// the record that follows. It returns false when the buffer does not contain
// the connection in a recognizable form.
func Calibrate(b []byte, local, remote netip.AddrPort, pid int) (Layout, bool) {
	recs := Records(b)
	var ports [4]byte
	binary.BigEndian.PutUint16(ports[0:], remote.Port())
	binary.BigEndian.PutUint16(ports[2:], local.Port())
	la := local.Addr().As4()
	ra := remote.Addr().As4()
	var pidLE [4]byte
	binary.LittleEndian.PutUint32(pidLE[:], uint32(pid))

	for i, r := range recs {
		p := indexOf(r.Data, ports[:], 8)
		if p < 0 {
			continue
		}
		// addresses: foreign first, local second (the unions are adjacent, 16 bytes each)
		fa := indexOf(r.Data, ra[:], p+4)
		if fa < 0 {
			continue
		}
		laOff := indexOf(r.Data, la[:], fa+4)
		if laOff < 0 {
			if la != ra {
				continue
			}
			// same address on both sides: it must sit exactly one union further
			if fa+16+4 <= len(r.Data) && [4]byte(r.Data[fa+16:fa+20]) == la {
				laOff = fa + 16
			} else {
				continue
			}
		}
		if i+1 >= len(recs) {
			continue
		}
		so := recs[i+1]
		pidOff := indexOf(so.Data, pidLE[:], 8)
		if pidOff < 0 {
			continue
		}
		lay := Layout{
			InpKind: r.Kind, SoKind: so.Kind,
			Fport: p, Lport: p + 2, Faddr4: fa, Laddr4: laOff, Pid: pidOff,
		}
		// the layout must reproduce the connection exactly
		for _, c := range ParsePcblistLayout(b, lay) {
			if c.Local == local && c.Remote == remote && c.PID == pid {
				return lay, true
			}
		}
	}
	return Layout{}, false
}

func indexOf(hay, needle []byte, from int) int {
	for i := from; i+len(needle) <= len(hay); i++ {
		match := true
		for j := range needle {
			if hay[i+j] != needle[j] {
				match = false
				break
			}
		}
		if match {
			return i
		}
	}
	return -1
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
