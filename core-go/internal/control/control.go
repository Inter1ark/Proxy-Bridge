// Package control implements the localhost control socket. A client sends one
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
}

// Server is the control listener.
type Server struct {
	ln       net.Listener
	opts     Options
	stopOnce sync.Once
	wg       sync.WaitGroup
	mu       sync.Mutex
	conns    map[net.Conn]struct{}
}

// Listen binds the control socket on 127.0.0.1:port.
func Listen(port int, opts Options) (*Server, error) {
	if opts.Token == "" {
		return nil, fmt.Errorf("control token must not be empty")
	}
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return nil, fmt.Errorf("control socket: %w", err)
	}
	s := &Server{ln: ln, opts: opts, conns: make(map[net.Conn]struct{})}
	s.wg.Add(1)
	go s.acceptLoop()
	return s, nil
}

// Addr returns the bound address.
func (s *Server) Addr() string { return s.ln.Addr().String() }

// ResetStop lets a later "stop" run OnStop again (daemon mode stops the
// session, not the process, so the command must stay usable).
func (s *Server) ResetStop() {
	s.mu.Lock()
	s.stopOnce = sync.Once{}
	s.mu.Unlock()
}

func (s *Server) acceptLoop() {
	defer s.wg.Done()
	for {
		c, err := s.ln.Accept()
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
	s.ln.Close()
	s.mu.Lock()
	for c := range s.conns {
		c.Close()
	}
	s.mu.Unlock()
	s.wg.Wait()
}
