// Package control implements the control socket (localhost TCP and, in daemon
// mode, a unix socket that VPN packet filters cannot block). A client sends one
// text line: "stop <token>" asks the core to shut down (or, in daemon mode, to
// bring the tunnel down), "watch <token>" keeps the connection open and streams
// status lines to it, "ping <token>" answers "ok <version>", "rules <token>
// <base64 json>" replaces the routing rules while running and "start <token>
// <base64 json>" brings the tunnel up (daemon mode only).
package control

import (
	"bufio"
	"crypto/subtle"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync"
	"time"

	"proxybridge/core/internal/status"
)

// Options configures the listener. OnRules and OnStart may be nil when the
// command is not supported.
type Options struct {
	Token    string
	Version  string
	Reporter *status.Reporter
	OnStop   func()
	OnRules  func(payload []byte) error
	OnStart  func(payload []byte) error
	// Socket is the path of an additional unix socket (empty: TCP only). Its file
	// is owned by root and SocketGroup with mode 0660, so only local users of that
	// group can connect.
	Socket      string
	SocketGroup int
}

// Server is the control listener.
type Server struct {
	ln       net.Listener
	lns      []net.Listener
	opts     Options
	stopOnce sync.Once
	wg       sync.WaitGroup
	mu       sync.Mutex
	conns    map[net.Conn]struct{}
}

// Listen binds the control socket on 127.0.0.1:port and, when opts.Socket is
// set, on that unix socket as well.
func Listen(port int, opts Options) (*Server, error) {
	if opts.Token == "" {
		return nil, fmt.Errorf("control token must not be empty")
	}
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return nil, fmt.Errorf("control socket: %w", err)
	}
	s := &Server{ln: ln, lns: []net.Listener{ln}, opts: opts, conns: make(map[net.Conn]struct{})}
	if opts.Socket != "" {
		uln, err := listenUnix(opts.Socket, opts.SocketGroup)
		if err != nil {
			ln.Close()
			return nil, err
		}
		s.lns = append(s.lns, uln)
	}
	for _, l := range s.lns {
		s.wg.Add(1)
		go s.acceptLoop(l)
	}
	return s, nil
}

// listenUnix replaces a stale socket file and restricts the new one to root and gid.
func listenUnix(path string, gid int) (net.Listener, error) {
	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		return nil, fmt.Errorf("control socket %s: %w", path, err)
	}
	uln, err := net.Listen("unix", path)
	if err != nil {
		return nil, fmt.Errorf("control socket %s: %w", path, err)
	}
	if err := os.Chmod(path, 0o660); err != nil {
		uln.Close()
		return nil, fmt.Errorf("control socket %s: %w", path, err)
	}
	if gid > 0 && os.Geteuid() == 0 {
		if err := os.Chown(path, 0, gid); err != nil {
			uln.Close()
			return nil, fmt.Errorf("control socket %s: %w", path, err)
		}
	}
	return uln, nil
}

// Addr returns the bound TCP address.
func (s *Server) Addr() string { return s.ln.Addr().String() }

// Addrs returns every bound address (TCP first, then the unix socket path).
func (s *Server) Addrs() string {
	parts := make([]string, 0, len(s.lns))
	for _, l := range s.lns {
		parts = append(parts, l.Addr().String())
	}
	return strings.Join(parts, ", ")
}

// ResetStop lets a later "stop" run OnStop again (daemon mode stops the
// session, not the process, so the command must stay usable).
func (s *Server) ResetStop() {
	s.mu.Lock()
	s.stopOnce = sync.Once{}
	s.mu.Unlock()
}

func (s *Server) acceptLoop(ln net.Listener) {
	defer s.wg.Done()
	for {
		c, err := ln.Accept()
		if err != nil {
			return
		}
		s.mu.Lock()
		s.conns[c] = struct{}{}
		s.mu.Unlock()
		go s.handle(c)
	}
}

func (s *Server) handle(c net.Conn) {
	defer func() {
		s.mu.Lock()
		delete(s.conns, c)
		s.mu.Unlock()
		c.Close()
	}()
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := bufio.NewReaderSize(c, 1<<20).ReadString('\n')
	if err != nil && line == "" {
		return
	}
	fields := strings.Fields(line)
	if len(fields) < 2 || subtle.ConstantTimeCompare([]byte(fields[1]), []byte(s.opts.Token)) != 1 {
		io.WriteString(c, "error unauthorized\n")
		return
	}
	switch fields[0] {
	case "rules", "start":
		handler := s.opts.OnRules
		if fields[0] == "start" {
			handler = s.opts.OnStart
		}
		if handler == nil {
			fmt.Fprintf(c, "error %s not supported\n", fields[0])
			return
		}
		if len(fields) != 3 {
			fmt.Fprintf(c, "error usage: %s <token> <base64 json>\n", fields[0])
			return
		}
		payload, err := base64.StdEncoding.DecodeString(fields[2])
		if err != nil {
			io.WriteString(c, "error bad base64\n")
			return
		}
		// Bringing a tunnel up can take a while: the client waits for the reply.
		_ = c.SetReadDeadline(time.Time{})
		if err := handler(payload); err != nil {
			fmt.Fprintf(c, "error %s\n", strings.ReplaceAll(err.Error(), "\n", " "))
			return
		}
		io.WriteString(c, "ok\n")
	case "ping":
		if s.opts.Version != "" {
			fmt.Fprintf(c, "ok %s\n", s.opts.Version)
		} else {
			io.WriteString(c, "ok\n")
		}
	case "stop":
		io.WriteString(c, "ok\n")
		s.mu.Lock()
		once := &s.stopOnce
		s.mu.Unlock()
		once.Do(func() { go s.opts.OnStop() })
	case "watch":
		_ = c.SetReadDeadline(time.Time{})
		s.opts.Reporter.Attach(c)
		// Block until the peer closes the connection.
		io.Copy(io.Discard, c)
		s.opts.Reporter.Detach(c)
	default:
		io.WriteString(c, "error unknown command\n")
	}
}

// Close stops accepting and drops all client connections.
func (s *Server) Close() {
	for _, l := range s.lns {
		l.Close()
	}
	s.mu.Lock()
	for c := range s.conns {
		c.Close()
	}
	s.mu.Unlock()
	s.wg.Wait()
}
