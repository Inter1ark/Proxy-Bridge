//go:build darwin || linux

package engine

import (
	"net/netip"
	"testing"
)

func TestEchoReplyPacket(t *testing.T) {
	request := []byte{8, 0, 0, 0, 0x12, 0x34, 0x00, 0x07, 'a', 'b', 'c'}
	// what came back from outside: another identifier (the system may rewrite it), same sequence
	reply := []byte{0, 0, 0xFF, 0xFF, 0x99, 0x99, 0x00, 0x07, 'a', 'b', 'c'}
	from, to := netip.MustParseAddr("10.8.0.1"), netip.MustParseAddr("10.255.0.2")
	pkt := echoReplyPacket(from, to, request, reply)
	if len(pkt) != 20+len(reply) || pkt[0] != 0x45 || pkt[9] != 1 {
		t.Fatalf("bad IP header: % x", pkt[:20])
	}
	if got := netip.AddrFrom4([4]byte(pkt[12:16])); got != from {
		t.Fatalf("source %s, want the pinged address %s", got, from)
	}
	if got := netip.AddrFrom4([4]byte(pkt[16:20])); got != to {
		t.Fatalf("destination %s, want %s", got, to)
	}
	if internetChecksum(pkt[:20]) != 0 {
		t.Fatal("IP header checksum does not verify")
	}
	icmp := pkt[20:]
	if icmp[0] != 0 || icmp[4] != 0x12 || icmp[5] != 0x34 || icmp[7] != 0x07 {
		t.Fatalf("echo reply must carry the request's identifier and sequence: % x", icmp[:8])
	}
	if internetChecksum(icmp) != 0 {
		t.Fatal("ICMP checksum does not verify")
	}
	if string(icmp[8:]) != "abc" {
		t.Fatalf("payload %q", icmp[8:])
	}
}

func TestForwardPingTakesOnlyEchoRequests(t *testing.T) {
	e := &Engine{} // no Ping function: nothing is taken
	echo := make([]byte, 28)
	echo[0], echo[9], echo[20] = 0x45, 1, 8
	if e.forwardPing(echo) {
		t.Fatal("without a Ping function the packet must go to the stack")
	}
}
