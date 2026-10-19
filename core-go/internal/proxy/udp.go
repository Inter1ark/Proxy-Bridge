package proxy

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"time"
)

const socksCmdUDPAssociate = 0x03

// ErrNoUDP is returned when the proxy refuses UDP ASSOCIATE (HTTP proxies,
// SOCKS5 servers without UDP support).
var ErrNoUDP = errors.New("proxy does not relay UDP")

// UDPAssoc is one SOCKS5 UDP association: a TCP control connection that keeps
// it alive and a UDP socket talking to the proxy's relay endpoint. Datagrams
// to any destination can be sent through it; replies carry their source.
type UDPAssoc struct {
	ctrl  net.Conn
	conn  net.PacketConn
	relay *net.UDPAddr
}

// UDPAssociate opens a UDP association through a SOCKS5 proxy. The UDP socket
// is created with base's Control (interface binding), like the TCP sockets.
func (d *Dialer) UDPAssociate(ctx context.Context) (*UDPAssoc, error) {
	if d.cfg.Kind != KindSOCKS5 {
		return nil, ErrNoUDP
	}
	nd := *d.base
	nd.Timeout = d.timeout
	ctrl, err := nd.DialContext(ctx, "tcp", d.serverAddr)
	if err != nil {
		return nil, fmt.Errorf("connect to proxy %s: %w", d.serverAddr, err)
	}
	_ = ctrl.SetDeadline(time.Now().Add(d.timeout))
	if err := socks5Auth(ctrl, d.cfg); err != nil {
		ctrl.Close()
		return nil, err
	}
	// DST.ADDR/PORT 0.0.0.0:0: the proxy accepts datagrams from the address the
	// control connection came from.
	if _, err := ctrl.Write([]byte{socksVersion, socksCmdUDPAssociate, 0x00, socksAtypIPv4, 0, 0, 0, 0, 0, 0}); err != nil {
		ctrl.Close()
		return nil, fmt.Errorf("socks5 udp associate: %w", err)
	}
	var head [4]byte
	if _, err := io.ReadFull(ctrl, head[:]); err != nil {
		ctrl.Close()
		return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
	}
	if head[0] != socksVersion {
		ctrl.Close()
		return nil, fmt.Errorf("socks5: bad reply version %d", head[0])
	}
	if head[1] != 0x00 {
		ctrl.Close()
		if head[1] == 0x07 {
			return nil, ErrNoUDP
		}
		return nil, fmt.Errorf("socks5: udp associate failed: %s", socksReplyText(head[1]))
	}
	var relayIP net.IP
	switch head[3] {
	case socksAtypIPv4:
		var a [4]byte
		if _, err := io.ReadFull(ctrl, a[:]); err != nil {
			ctrl.Close()
			return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
		}
		relayIP = net.IP(a[:])
	case socksAtypIPv6:
		var a [16]byte
		if _, err := io.ReadFull(ctrl, a[:]); err != nil {
			ctrl.Close()
			return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
		}
		relayIP = net.IP(a[:])
	case socksAtypDomain:
		var l [1]byte
		if _, err := io.ReadFull(ctrl, l[:]); err != nil {
			ctrl.Close()
			return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
		}
		name := make([]byte, int(l[0]))
		if _, err := io.ReadFull(ctrl, name); err != nil {
			ctrl.Close()
			return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
		}
		ips, err := net.DefaultResolver.LookupIP(ctx, "ip4", string(name))
		if err != nil || len(ips) == 0 {
			ctrl.Close()
			return nil, fmt.Errorf("socks5: relay host %q: %v", name, err)
		}
		relayIP = ips[0]
	default:
		ctrl.Close()
		return nil, fmt.Errorf("socks5: bad reply address type %d", head[3])
	}
	var p [2]byte
	if _, err := io.ReadFull(ctrl, p[:]); err != nil {
		ctrl.Close()
		return nil, fmt.Errorf("socks5 udp associate reply: %w", err)
	}
	relayPort := int(p[0])<<8 | int(p[1])
	if relayIP.IsUnspecified() {
		// RFC 1928 allows 0.0.0.0: use the proxy server's address.
		host, _, _ := net.SplitHostPort(d.serverAddr)
		relayIP = net.ParseIP(host)
	}
	if relayPort == 0 || relayIP == nil {
		ctrl.Close()
		return nil, errors.New("socks5: udp associate reply without a relay endpoint")
	}
	_ = ctrl.SetDeadline(time.Time{})

	lc := net.ListenConfig{Control: d.base.Control}
	network := "udp4"
	if relayIP.To4() == nil {
		network = "udp6"
	}
	conn, err := lc.ListenPacket(ctx, network, ":0")
	if err != nil {
		ctrl.Close()
		return nil, fmt.Errorf("udp socket: %w", err)
	}
	a := &UDPAssoc{ctrl: ctrl, conn: conn, relay: &net.UDPAddr{IP: relayIP, Port: relayPort}}
	// The control connection carries nothing; reading it only tells us when the
	// proxy drops the association.
	go func() {
		var b [1]byte
		_, _ = ctrl.Read(b[:])
		conn.Close()
	}()
	return a, nil
}

// WriteTo sends one datagram to dst through the association.
func (a *UDPAssoc) WriteTo(payload []byte, dst netip.AddrPort) error {
	_, err := a.conn.WriteTo(EncodeUDP(dst, payload), a.relay)
	return err
}

// ReadFrom receives one datagram; src is the remote peer it came from.
// deadline bounds the wait (zero: none).
func (a *UDPAssoc) ReadFrom(buf []byte, deadline time.Time) (int, netip.AddrPort, error) {
	_ = a.conn.SetReadDeadline(deadline)
	for {
		n, from, err := a.conn.ReadFrom(buf)
		if err != nil {
			return 0, netip.AddrPort{}, err
		}
		if ua, ok := from.(*net.UDPAddr); ok && !ua.IP.Equal(a.relay.IP) {
			continue // not from the relay
		}
		src, payload, err := DecodeUDP(buf[:n])
		if err != nil {
			continue
		}
		n = copy(buf, payload)
		return n, src, nil
	}
}

// Close drops the association.
func (a *UDPAssoc) Close() error {
	a.conn.Close()
	return a.ctrl.Close()
}

// EncodeUDP builds a SOCKS5 UDP request header (RSV, FRAG, ATYP, DST.ADDR,
// DST.PORT) followed by the payload.
func EncodeUDP(dst netip.AddrPort, payload []byte) []byte {
	ip := dst.Addr().Unmap()
	var out []byte
	if ip.Is4() {
		a := ip.As4()
		out = make([]byte, 0, 10+len(payload))
		out = append(out, 0, 0, 0, socksAtypIPv4)
		out = append(out, a[:]...)
	} else {
		a := ip.As16()
		out = make([]byte, 0, 22+len(payload))
		out = append(out, 0, 0, 0, socksAtypIPv6)
		out = append(out, a[:]...)
	}
	out = append(out, byte(dst.Port()>>8), byte(dst.Port()))
	return append(out, payload...)
}

// DecodeUDP parses a SOCKS5 UDP reply. Fragmented datagrams are rejected.
func DecodeUDP(pkt []byte) (src netip.AddrPort, payload []byte, err error) {
	if len(pkt) < 10 || pkt[2] != 0 {
		return src, nil, errors.New("socks5 udp: short or fragmented datagram")
	}
	switch pkt[3] {
	case socksAtypIPv4:
		src = netip.AddrPortFrom(netip.AddrFrom4([4]byte(pkt[4:8])), uint16(pkt[8])<<8|uint16(pkt[9]))
		return src, pkt[10:], nil
	case socksAtypIPv6:
		if len(pkt) < 22 {
			return src, nil, errors.New("socks5 udp: short datagram")
		}
		src = netip.AddrPortFrom(netip.AddrFrom16([16]byte(pkt[4:20])), uint16(pkt[20])<<8|uint16(pkt[21]))
		return src, pkt[22:], nil
	case socksAtypDomain:
		l := int(pkt[4])
		if len(pkt) < 5+l+2 {
			return src, nil, errors.New("socks5 udp: short datagram")
		}
		// a domain source cannot be matched to a flow; report it unspecified
		return netip.AddrPortFrom(netip.IPv4Unspecified(), uint16(pkt[5+l])<<8|uint16(pkt[6+l])), pkt[7+l:], nil
	default:
		return src, nil, fmt.Errorf("socks5 udp: bad address type %d", pkt[3])
	}
}
