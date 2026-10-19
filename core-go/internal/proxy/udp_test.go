package proxy

import (
	"net/netip"
	"testing"
)

func TestUDPEncodeDecode(t *testing.T) {
	dst := netip.MustParseAddrPort("74.125.250.129:19302")
	pkt := EncodeUDP(dst, []byte("hello"))
	if len(pkt) != 10+5 || pkt[3] != socksAtypIPv4 || pkt[8] != 0x4B || pkt[9] != 0x66 {
		t.Fatalf("encode: % x", pkt)
	}
	src, payload, err := DecodeUDP(pkt)
	if err != nil || src != dst || string(payload) != "hello" {
		t.Fatalf("decode: %v %s %q", err, src, payload)
	}
	dst6 := netip.MustParseAddrPort("[2001:db8::1]:53")
	pkt = EncodeUDP(dst6, []byte{1, 2})
	src, payload, err = DecodeUDP(pkt)
	if err != nil || src != dst6 || len(payload) != 2 {
		t.Fatalf("decode v6: %v %s %q", err, src, payload)
	}
	if _, _, err := DecodeUDP([]byte{0, 0, 1, 1, 1, 2, 3, 4, 0, 53, 9}); err == nil {
		t.Fatal("fragmented datagram must be rejected")
	}
	if _, _, err := DecodeUDP([]byte{0, 0, 0}); err == nil {
		t.Fatal("short datagram must be rejected")
	}
	src, payload, err = DecodeUDP(append([]byte{0, 0, 0, socksAtypDomain, 3, 'd', 'n', 's', 0, 53}, 'x'))
	if err != nil || src.Port() != 53 || string(payload) != "x" {
		t.Fatalf("decode domain: %v %s %q", err, src, payload)
	}
}
