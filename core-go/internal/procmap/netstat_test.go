package procmap

import (
	"net/netip"
	"testing"
)

func TestParseNetstat(t *testing.T) {
	out := "Active Internet connections (including servers)\n" +
		"Proto Recv-Q Send-Q  Local Address          Foreign Address        (state)     rhiwat  shiwat    pid   epid  state    options           gencnt    flags   flags1 usscnt rtncnt fltrs\n" +
		"tcp4       0      0  127.0.0.1.49320        127.0.0.1.49319        ESTABLISHED 408300  146988   4242      0 0x0102 0x00000020 0x00000000000003e8 00000080 00000000      1      0 000001\n" +
		"tcp4       0      0  10.255.0.2.49479       104.26.12.205.443      SYN_SENT    131072  131072    777      0 0x0000 0x00000000 0x00000000000003e9 00000080 00000000      1      0 000001\n" +
		"tcp46      0      0  *.34050                *.*                    LISTEN      131072  131072    999      0 0x0000 0x00000000 0x00000000000003ea 00000080 00000000      1      0 000001\n" +
		"tcp4       0      0  *.22                   *.*                    LISTEN      131072  131072    123      0 0x0000 0x00000000 0x00000000000003eb 00000080 00000000      1      0 000001\n"
	conns, err := ParseNetstat(out)
	if err != nil {
		t.Fatal(err)
	}
	if len(conns) != 4 {
		t.Fatalf("got %d: %+v", len(conns), conns)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("127.0.0.1:49320"), netip.MustParseAddrPort("127.0.0.1:49319")); !ok || pid != 4242 {
		t.Fatalf("self: %d %v", pid, ok)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:49479"), netip.MustParseAddrPort("104.26.12.205:443")); !ok || pid != 777 {
		t.Fatalf("flow: %d %v", pid, ok)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("0.0.0.0:34050"), netip.AddrPort{}); !ok || pid != 999 {
		t.Fatalf("listener: %d %v", pid, ok)
	}
	if _, err := ParseNetstat("no header here\n"); err == nil {
		t.Fatal("expected header error")
	}
}
