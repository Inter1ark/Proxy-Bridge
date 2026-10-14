package procmap

import (
	"encoding/binary"
	"net/netip"
	"testing"
)

func record(kind uint32, size int, fill func(b []byte)) []byte {
	b := make([]byte, size)
	binary.LittleEndian.PutUint32(b[0:], uint32(size))
	binary.LittleEndian.PutUint32(b[4:], kind)
	fill(b)
	return b
}

func inpcb(local, remote netip.AddrPort, v4 bool) []byte {
	return record(xsoInpcb, 108, func(b []byte) {
		binary.BigEndian.PutUint16(b[inpLport:], local.Port())
		binary.BigEndian.PutUint16(b[inpFport:], remote.Port())
		if v4 {
			b[inpVflag] = inpIPv4
		} else {
			b[inpVflag] = 0x2
		}
		la := local.Addr().As4()
		fa := remote.Addr().As4()
		copy(b[inpLaddr+12:], la[:])
		copy(b[inpFaddr+12:], fa[:])
	})
}

func socket(pid int32) []byte {
	return record(xsoSocket, 80, func(b []byte) {
		binary.LittleEndian.PutUint32(b[soLastPid:], uint32(pid))
	})
}

func TestParsePcblist(t *testing.T) {
	gen := record(0, 24, func([]byte) {})
	a := netip.MustParseAddrPort("10.255.0.2:51000")
	ar := netip.MustParseAddrPort("1.2.3.4:443")
	b := netip.MustParseAddrPort("10.255.0.2:51001")
	br := netip.MustParseAddrPort("5.6.7.8:80")
	v6 := netip.MustParseAddrPort("0.0.0.0:51002")

	var buf []byte
	buf = append(buf, gen...)
	buf = append(buf, inpcb(a, ar, true)...)
	buf = append(buf, socket(1001)...)
	buf = append(buf, record(xsoTcpcb, 40, func([]byte) {})...)
	buf = append(buf, inpcb(v6, br, false)...) // IPv6: skipped
	buf = append(buf, socket(1002)...)
	buf = append(buf, inpcb(b, br, true)...)
	buf = append(buf, socket(0)...) // unknown owner: skipped
	buf = append(buf, inpcb(b, br, true)...)
	buf = append(buf, socket(1003)...)
	buf = append(buf, gen...)

	conns := ParsePcblist(buf)
	if len(conns) != 2 {
		t.Fatalf("got %d conns: %+v", len(conns), conns)
	}
	if conns[0].PID != 1001 || conns[0].Local != a || conns[0].Remote != ar {
		t.Fatalf("conn0: %+v", conns[0])
	}
	if conns[1].PID != 1003 || conns[1].Local != b {
		t.Fatalf("conn1: %+v", conns[1])
	}

	if pid, ok := Find(conns, a, ar); !ok || pid != 1001 {
		t.Fatalf("find a: %d %v", pid, ok)
	}
	// remote mismatch but same local endpoint: still found (fallback)
	if pid, ok := Find(conns, b, netip.MustParseAddrPort("9.9.9.9:9")); !ok || pid != 1003 {
		t.Fatalf("find b fallback: %d %v", pid, ok)
	}
	// unknown local address (0.0.0.0) matches by port
	if pid, ok := Find(conns, netip.MustParseAddrPort("0.0.0.0:51000"), ar); !ok || pid != 1001 {
		t.Fatalf("find wildcard: %d %v", pid, ok)
	}
	if _, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:1"), ar); ok {
		t.Fatal("port 1 must not be found")
	}
}

func TestParsePcblistTruncated(t *testing.T) {
	buf := inpcb(netip.MustParseAddrPort("10.0.0.1:1"), netip.MustParseAddrPort("10.0.0.2:2"), true)
	// a record that claims to be longer than the buffer stops parsing without panicking
	binary.LittleEndian.PutUint32(buf[0:], 4096)
	if got := ParsePcblist(buf); len(got) != 0 {
		t.Fatalf("got %+v", got)
	}
	if got := ParsePcblist(buf[:5]); len(got) != 0 {
		t.Fatalf("got %+v", got)
	}
}
