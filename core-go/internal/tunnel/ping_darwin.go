//go:build darwin

package tunnel

import (
	"errors"
	"net"
	"net/netip"
	"time"

	"golang.org/x/sys/unix"
)

// pingTimeout bounds one forwarded echo request.
const pingTimeout = 3 * time.Second

// pingThrough sends one ICMP echo request (msg starts at the ICMP type byte) to dst through
// the named interface and returns the echo reply, also starting at the type byte. It uses a
// datagram ICMP socket, which needs no raw socket rights, scoped to the interface so the
// request does not come back into the tunnel.
func pingThrough(ifName string, dst netip.Addr, msg []byte) ([]byte, error) {
	if !dst.Is4() || len(msg) < 8 {
		return nil, errors.New("ping: not an IPv4 echo request")
	}
	iface, err := net.InterfaceByName(ifName)
	if err != nil {
		return nil, unix.ENXIO
	}
	fd, err := unix.Socket(unix.AF_INET, unix.SOCK_DGRAM, unix.IPPROTO_ICMP)
	if err != nil {
		return nil, err
	}
	defer unix.Close(fd)
	if err := unix.SetsockoptInt(fd, unix.IPPROTO_IP, unix.IP_BOUND_IF, iface.Index); err != nil {
		return nil, err
	}
	tv := unix.NsecToTimeval(int64(pingTimeout))
	_ = unix.SetsockoptTimeval(fd, unix.SOL_SOCKET, unix.SO_RCVTIMEO, &tv)
	to := &unix.SockaddrInet4{Addr: dst.As4()}
	if err := unix.Sendto(fd, msg, 0, to); err != nil {
		return nil, err
	}
	deadline := time.Now().Add(pingTimeout)
	buf := make([]byte, 65535)
	for time.Now().Before(deadline) {
		n, from, err := unix.Recvfrom(fd, buf, 0)
		if err != nil {
			return nil, err
		}
		reply := buf[:n]
		// macOS hands the datagram over with its IP header
		if n >= 20 && reply[0]>>4 == 4 {
			ihl := int(reply[0]&0x0F) * 4
			if ihl < 20 || n < ihl+8 {
				continue
			}
			reply = reply[ihl:]
		}
		if len(reply) < 8 || reply[0] != 0 { // echo reply only
			continue
		}
		if sa, ok := from.(*unix.SockaddrInet4); ok && sa.Addr != dst.As4() {
			continue
		}
		if reply[6] != msg[6] || reply[7] != msg[7] { // sequence number
			continue
		}
		return append([]byte(nil), reply...), nil
	}
	return nil, unix.ETIMEDOUT
}
