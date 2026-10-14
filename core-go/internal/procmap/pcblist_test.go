package procmap

import (
	"encoding/binary"
	"net/netip"
	"testing"
)

// record builds one stream entry: the length field holds the declared size,
// the entry itself is padded to 8 bytes like the kernel does.
func record(kind uint32, size int, fill func(b []byte)) []byte {
	b := make([]byte, (size+7)&^7)
	binary.LittleEndian.PutUint32(b[0:], uint32(size))
	binary.LittleEndian.PutUint32(b[4:], kind)
	fill(b[:size])
	return b
}

func inpcb(local, remote netip.AddrPort, v4 bool) []byte {
	return record(DefaultLayout.InpKind, 108, func(b []byte) {
		binary.BigEndian.PutUint16(b[DefaultLayout.Lport:], local.Port())
		binary.BigEndian.PutUint16(b[DefaultLayout.Fport:], remote.Port())
		if v4 {
			b[48] = 1
		} else {
			b[48] = 2
		}
		la := local.Addr().As4()
		fa := remote.Addr().As4()
		copy(b[DefaultLayout.Laddr4:], la[:])
		copy(b[DefaultLayout.Faddr4:], fa[:])
	})
}

func socket(pid int32) []byte {
	return record(DefaultLayout.SoKind, 80, func(b []byte) {
		binary.LittleEndian.PutUint32(b[DefaultLayout.Pid:], uint32(pid))
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
	buf = append(buf, record(uint32(0x20), 40, func([]byte) {})...)
	buf = append(buf, inpcb(v6, br, false)...) // an IPv6 entry parses as a wildcard IPv4 entry; harmless
	buf = append(buf, socket(1002)...)
	buf = append(buf, inpcb(b, br, true)...)
	buf = append(buf, socket(0)...) // unknown owner: skipped
	buf = append(buf, inpcb(b, br, true)...)
	buf = append(buf, socket(1003)...)
	buf = append(buf, gen...)

	conns := ParsePcblist(buf)
	if len(conns) != 3 {
		t.Fatalf("got %d conns: %+v", len(conns), conns)
	}
	if conns[0].PID != 1001 || conns[0].Local != a || conns[0].Remote != ar {
		t.Fatalf("conn0: %+v", conns[0])
	}
	if conns[2].PID != 1003 || conns[2].Local != b {
		t.Fatalf("conn2: %+v", conns[2])
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

func TestCalibrate(t *testing.T) {
	local := netip.MustParseAddrPort("127.0.0.1:49319")
	remote := netip.MustParseAddrPort("127.0.0.1:49318")
	// an unusual layout: ports at 20, addresses at 40/56, pid at 60, kinds 0x33/0x44
	inp := record(0x33, 100, func(b []byte) {
		binary.BigEndian.PutUint16(b[20:], remote.Port())
		binary.BigEndian.PutUint16(b[22:], local.Port())
		copy(b[40:], []byte{127, 0, 0, 1})
		copy(b[56:], []byte{127, 0, 0, 1})
	})
	so := record(0x44, 80, func(b []byte) { binary.LittleEndian.PutUint32(b[60:], 4242) })
	other := record(0x33, 100, func(b []byte) {
		binary.BigEndian.PutUint16(b[20:], 443)
		binary.BigEndian.PutUint16(b[22:], 51000)
		copy(b[40:], []byte{1, 2, 3, 4})
		copy(b[56:], []byte{10, 255, 0, 2})
	})
	so2 := record(0x44, 80, func(b []byte) { binary.LittleEndian.PutUint32(b[60:], 777) })
	var buf []byte
	buf = append(buf, record(0, 24, func([]byte) {})...)
	buf = append(buf, other...)
	buf = append(buf, so2...)
	buf = append(buf, inp...)
	buf = append(buf, so...)
	lay, ok := Calibrate(buf, local, remote, 4242)
	if !ok {
		t.Fatal("calibration failed")
	}
	if lay.Fport != 20 || lay.Lport != 22 || lay.Faddr4 != 40 || lay.Laddr4 != 56 || lay.Pid != 60 || lay.InpKind != 0x33 || lay.SoKind != 0x44 {
		t.Fatalf("layout %+v", lay)
	}
	conns := ParsePcblistLayout(buf, lay)
	if pid, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:51000"), netip.MustParseAddrPort("1.2.3.4:443")); !ok || pid != 777 {
		t.Fatalf("find: %d %v", pid, ok)
	}
	if _, ok := Calibrate(buf, local, remote, 1); ok {
		t.Fatal("wrong pid must not calibrate")
	}
	// the default layout calibrates against a buffer built with the default offsets
	a := netip.MustParseAddrPort("127.0.0.1:1000")
	b := netip.MustParseAddrPort("127.0.0.1:2000")
	var buf2 []byte
	buf2 = append(buf2, inpcb(a, b, true)...)
	buf2 = append(buf2, socket(99)...)
	lay2, ok := Calibrate(buf2, a, b, 99)
	if !ok || lay2 != DefaultLayout {
		t.Fatalf("default layout: %+v %v", lay2, ok)
	}
}

func TestRecordsAligned(t *testing.T) {
	// a 36-byte record is followed by 4 bytes of padding before the next one
	a := record(0x10, 36, func([]byte) {})
	if len(a) != 40 {
		t.Fatalf("helper must pad: %d", len(a))
	}
	b := record(0x01, 80, func(b []byte) { binary.LittleEndian.PutUint32(b[8:], 7) })
	buf := append(append([]byte{}, a...), b...)
	recs := Records(buf)
	if len(recs) != 2 || recs[1].Kind != 0x01 || len(recs[1].Data) != 80 || len(recs[0].Data) != 36 {
		t.Fatalf("records: %+v", recs)
	}
}
