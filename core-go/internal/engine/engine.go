//go:build darwin || linux

// Package engine runs a userspace TCP/IP stack on top of a TUN device and
// relays every TCP flow through the configured upstream dialer. UDP is
// dropped except DNS (port 53), which is forwarded directly to one resolver.
package engine

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"golang.zx2c4.com/wireguard/tun"
	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv6"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/tcpip/transport/udp"
	"gvisor.dev/gvisor/pkg/waiter"
)

const (
	nicID = tcpip.NICID(1)
	// tunOffset is the headroom kept in front of every packet buffer. The
	// darwin utun driver needs 4 bytes for its address family header.
	tunOffset = 16
	// relayLinger bounds how long a half-closed relay waits for the other
	// direction to finish.
	relayLinger = 2 * time.Minute
	dnsIdle     = 30 * time.Second
)

// DialFunc opens a TCP connection to target (ip:port) through the proxy.
type DialFunc func(ctx context.Context, target string) (net.Conn, error)

// Counters holds traffic totals in bytes. Up is client to proxy, Down is proxy to client.
type Counters struct {
	Up   atomic.Int64
	Down atomic.Int64
}

// Options configures the engine.
type Options struct {
	MTU uint32
	// Dial is used for every TCP flow.
	Dial DialFunc
	// DNS is the ip:port of the resolver that udp/53 flows are forwarded to.
	// Empty disables DNS forwarding (DNS over UDP is then dropped).
	DNS string
	// Logf receives diagnostic messages.
	Logf func(format string, args ...any)
	// Stats receives byte counters.
	Stats *Counters
}

// Engine is a running stack.
type Engine struct {
	opts   Options
	dev    tun.Device
	stack  *stack.Stack
	ep     *channel.Endpoint
	ctx    context.Context
	cancel context.CancelFunc
	wg     sync.WaitGroup
	closed atomic.Bool
}

// New creates the stack attached to dev. Call Start to begin forwarding.
func New(dev tun.Device, opts Options) (*Engine, error) {
	if opts.Dial == nil {
		return nil, errors.New("engine: Dial is required")
	}
	if opts.Logf == nil {
		opts.Logf = func(string, ...any) {}
	}
	if opts.Stats == nil {
		opts.Stats = &Counters{}
	}
	if opts.MTU == 0 {
		opts.MTU = 1500
	}

	s := stack.New(stack.Options{
		NetworkProtocols:   []stack.NetworkProtocolFactory{ipv4.NewProtocol, ipv6.NewProtocol},
		TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol, udp.NewProtocol},
	})

	ep := channel.New(1024, opts.MTU, "")
	if err := s.CreateNIC(nicID, ep); err != nil {
		return nil, fmt.Errorf("engine: create nic: %v", err)
	}
	// Accept packets for any destination address and allow replies from any
	// source address: the stack terminates flows on behalf of the whole internet.
	if err := s.SetPromiscuousMode(nicID, true); err != nil {
		return nil, fmt.Errorf("engine: promiscuous mode: %v", err)
	}
	if err := s.SetSpoofing(nicID, true); err != nil {
		return nil, fmt.Errorf("engine: spoofing: %v", err)
	}
	s.SetRouteTable([]tcpip.Route{
		{Destination: header.IPv4EmptySubnet, NIC: nicID},
		{Destination: header.IPv6EmptySubnet, NIC: nicID},
	})

	sack := tcpip.TCPSACKEnabled(true)
	if err := s.SetTransportProtocolOption(tcp.ProtocolNumber, &sack); err != nil {
		opts.Logf("engine: set SACK: %v", err)
	}
	moderate := tcpip.TCPModerateReceiveBufferOption(true)
	if err := s.SetTransportProtocolOption(tcp.ProtocolNumber, &moderate); err != nil {
		opts.Logf("engine: set moderate rcv buffer: %v", err)
	}
	rcv := tcpip.TCPReceiveBufferSizeRangeOption{Min: 4096, Default: 1 << 20, Max: 8 << 20}
	if err := s.SetTransportProtocolOption(tcp.ProtocolNumber, &rcv); err != nil {
		opts.Logf("engine: set rcv buffer range: %v", err)
	}
	snd := tcpip.TCPSendBufferSizeRangeOption{Min: 4096, Default: 1 << 20, Max: 8 << 20}
	if err := s.SetTransportProtocolOption(tcp.ProtocolNumber, &snd); err != nil {
		opts.Logf("engine: set snd buffer range: %v", err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	e := &Engine{opts: opts, dev: dev, stack: s, ep: ep, ctx: ctx, cancel: cancel}

	tcpFwd := tcp.NewForwarder(s, 0, 4096, e.handleTCP)
	s.SetTransportProtocolHandler(tcp.ProtocolNumber, tcpFwd.HandlePacket)
	udpFwd := udp.NewForwarder(s, e.handleUDP)
	s.SetTransportProtocolHandler(udp.ProtocolNumber, udpFwd.HandlePacket)

	return e, nil
}

// Start launches the TUN read and write loops.
func (e *Engine) Start() {
	e.wg.Add(2)
	go e.tunToStack()
	go e.stackToTun()
}

// Close stops the engine, releases the stack and closes the TUN device
// (closing the device is what unblocks the reader goroutine).
func (e *Engine) Close() {
	if !e.closed.CompareAndSwap(false, true) {
		return
	}
	e.cancel()
	e.ep.Close()
	e.stack.Close()
	_ = e.dev.Close()
	e.wg.Wait()
}

// Done is closed when the engine stops on its own (for example when the TUN device is gone).
func (e *Engine) Done() <-chan struct{} { return e.ctx.Done() }

func (e *Engine) tunToStack() {
	defer e.wg.Done()
	defer e.cancel()
	batch := e.dev.BatchSize()
	if batch < 1 {
		batch = 1
	}
	bufs := make([][]byte, batch)
	sizes := make([]int, batch)
	for i := range bufs {
		bufs[i] = make([]byte, tunOffset+int(e.opts.MTU)+64)
	}
	for e.ctx.Err() == nil {
		n, err := e.dev.Read(bufs, sizes, tunOffset)
		if err != nil {
			if e.ctx.Err() == nil && !errors.Is(err, io.EOF) {
				e.opts.Logf("tun read: %v", err)
			}
			return
		}
		for i := 0; i < n; i++ {
			pkt := bufs[i][tunOffset : tunOffset+sizes[i]]
			if len(pkt) == 0 {
				continue
			}
			var proto tcpip.NetworkProtocolNumber
			switch pkt[0] >> 4 {
			case 4:
				proto = header.IPv4ProtocolNumber
			case 6:
				proto = header.IPv6ProtocolNumber
			default:
				continue
			}
			data := make([]byte, len(pkt))
			copy(data, pkt)
			pb := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(data)})
			e.ep.InjectInbound(proto, pb)
			pb.DecRef()
		}
	}
}

func (e *Engine) stackToTun() {
	defer e.wg.Done()
	buf := make([]byte, tunOffset+int(e.opts.MTU)+64)
	for {
		pb := e.ep.ReadContext(e.ctx)
		if pb == nil {
			return
		}
		view := pb.ToView()
		data := view.AsSlice()
		if len(data) > len(buf)-tunOffset {
			view.Release()
			pb.DecRef()
			continue
		}
		n := copy(buf[tunOffset:], data)
		view.Release()
		pb.DecRef()
		if _, err := e.dev.Write([][]byte{buf[:tunOffset+n]}, tunOffset); err != nil {
			if e.ctx.Err() == nil {
				e.opts.Logf("tun write: %v", err)
			}
		}
	}
}

func (e *Engine) handleTCP(r *tcp.ForwarderRequest) {
	id := r.ID()
	target := net.JoinHostPort(id.LocalAddress.String(), strconv.Itoa(int(id.LocalPort)))
	go func() {
		dialCtx, cancel := context.WithTimeout(e.ctx, 20*time.Second)
		upstream, err := e.opts.Dial(dialCtx, target)
		cancel()
		if err != nil {
			e.opts.Logf("tcp %s: %v", target, err)
			r.Complete(true)
			return
		}
		var wq waiter.Queue
		ep, tcpErr := r.CreateEndpoint(&wq)
		if tcpErr != nil {
			e.opts.Logf("tcp %s: create endpoint: %v", target, tcpErr)
			upstream.Close()
			r.Complete(true)
			return
		}
		r.Complete(false)
		client := gonet.NewTCPConn(&wq, ep)
		e.relay(client, upstream)
	}()
}

// relay copies data in both directions and closes both connections when done.
func (e *Engine) relay(client, upstream net.Conn) {
	defer client.Close()
	defer upstream.Close()

	done := make(chan struct{}, 2)
	go func() {
		copyCount(upstream, client, &e.opts.Stats.Up)
		closeWrite(upstream)
		done <- struct{}{}
	}()
	go func() {
		copyCount(client, upstream, &e.opts.Stats.Down)
		closeWrite(client)
		done <- struct{}{}
	}()

	select {
	case <-done:
	case <-e.ctx.Done():
		return
	}
	// One direction finished; give the other a bounded time to drain.
	timer := time.NewTimer(relayLinger)
	defer timer.Stop()
	select {
	case <-done:
	case <-timer.C:
	case <-e.ctx.Done():
	}
}

func copyCount(dst io.Writer, src io.Reader, counter *atomic.Int64) {
	buf := make([]byte, 32*1024)
	for {
		n, err := src.Read(buf)
		if n > 0 {
			counter.Add(int64(n))
			if _, werr := dst.Write(buf[:n]); werr != nil {
				return
			}
		}
		if err != nil {
			return
		}
	}
}

func closeWrite(c net.Conn) {
	if cw, ok := c.(interface{ CloseWrite() error }); ok {
		_ = cw.CloseWrite()
		return
	}
	c.Close()
}

// handleUDP implements TCP-only mode: every UDP flow is rejected except DNS,
// which is forwarded directly to the configured resolver so name resolution
// keeps working. Returning false lets the stack answer with ICMP port
// unreachable, so applications fall back to TCP quickly instead of timing out.
func (e *Engine) handleUDP(r *udp.ForwarderRequest) bool {
	id := r.ID()
	if id.LocalPort != 53 || e.opts.DNS == "" {
		return false
	}
	var wq waiter.Queue
	ep, err := r.CreateEndpoint(&wq)
	if err != nil {
		e.opts.Logf("udp dns: create endpoint: %v", err)
		return true
	}
	conn := gonet.NewUDPConn(&wq, ep)
	go e.forwardDNS(conn)
	return true
}

func (e *Engine) forwardDNS(client *gonet.UDPConn) {
	defer client.Close()
	up, err := net.Dial("udp", e.opts.DNS)
	if err != nil {
		e.opts.Logf("udp dns: dial %s: %v", e.opts.DNS, err)
		return
	}
	defer up.Close()

	go func() {
		buf := make([]byte, 65535)
		for {
			_ = up.SetReadDeadline(time.Now().Add(dnsIdle))
			n, err := up.Read(buf)
			if err != nil {
				client.Close()
				return
			}
			if _, err := client.Write(buf[:n]); err != nil {
				return
			}
			e.opts.Stats.Down.Add(int64(n))
		}
	}()

	buf := make([]byte, 65535)
	for {
		_ = client.SetReadDeadline(time.Now().Add(dnsIdle))
		n, err := client.Read(buf)
		if err != nil {
			return
		}
		if _, err := up.Write(buf[:n]); err != nil {
			return
		}
		e.opts.Stats.Up.Add(int64(n))
	}
}
