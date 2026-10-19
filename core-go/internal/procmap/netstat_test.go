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

func TestParseNetstatProcessPid(t *testing.T) {
	out := "Active Internet connections (including servers)\n" +
		"Proto Recv-Q Send-Q  Local Address                                 Foreign Address                               (state)          rxbytes      txbytes  rhiwat  shiwat          process:pid    state  options           gencnt    flags   flags1 usecnt rtncnt fltrs\n" +
		"tcp4       0      0  192.168.64.4.49332     185.184.192.44.10772   SYN_SENT               0            0  131072  131072           pbcore:10632  00184 00000000 0000000000002315 00000080 04000900      2      0 000000\n" +
		"tcp4       0      0  10.255.0.2.49331       17.57.144.137.5223     SYN_SENT               0            0  131072  131072             apsd:137    00184 00020000 0000000000002314 00100001 04080900      2      0 000000\n"
	conns, err := ParseNetstat(out)
	if err != nil {
		t.Fatal(err)
	}
	if len(conns) != 2 {
		t.Fatalf("got %d: %+v", len(conns), conns)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:49331"), netip.MustParseAddrPort("17.57.144.137:5223")); !ok || pid != 137 {
		t.Fatalf("apsd: %d %v", pid, ok)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("192.168.64.4:49332"), netip.MustParseAddrPort("185.184.192.44:10772")); !ok || pid != 10632 {
		t.Fatalf("pbcore: %d %v", pid, ok)
	}
}

func TestParseNetstatUDP(t *testing.T) {
	out := "Active Internet connections (including servers)\n" +
		"Proto Recv-Q Send-Q  Local Address          Foreign Address        rhiwat  shiwat    pid   epid  state    options           gencnt    flags   flags1 usscnt rtncnt fltrs\n" +
		"udp4       0      0  *.5353                 *.*                    786896    9216    207      0 0x0000 0x00000000 0x0000000000000abc 00000000 00000000      1      0 000001\n" +
		"udp4       0      0  10.255.0.2.61234       *.*                    786896    9216   4242      0 0x0000 0x00000000 0x0000000000000abd 00000000 00000000      1      0 000001\n" +
		"udp46      0      0  *.49152                *.*                    786896    9216    999      0 0x0000 0x00000000 0x0000000000000abe 00000000 00000000      1      0 000001\n"
	conns, err := ParseNetstat(out)
	if err != nil {
		t.Fatal(err)
	}
	if len(conns) != 3 {
		t.Fatalf("got %d: %+v", len(conns), conns)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:61234"), netip.MustParseAddrPort("8.8.8.8:443")); !ok || pid != 4242 {
		t.Fatalf("bound socket: %d %v", pid, ok)
	}
	if pid, ok := Find(conns, netip.MustParseAddrPort("10.255.0.2:5353"), netip.MustParseAddrPort("224.0.0.251:5353")); !ok || pid != 207 {
		t.Fatalf("wildcard socket: %d %v", pid, ok)
	}
	// a TCP table with the state column still parses the pid from the right column
	tcp := "Proto Recv-Q Send-Q  Local Address          Foreign Address        (state)     rhiwat  shiwat    pid   epid\n" +
		"tcp4       0      0  10.255.0.2.49479       104.26.12.205.443      SYN_SENT    131072  131072    777      0\n"
	conns, err = ParseNetstat(tcp)
	if err != nil || len(conns) != 1 || conns[0].PID != 777 {
		t.Fatalf("tcp: %v %+v", err, conns)
	}
}
