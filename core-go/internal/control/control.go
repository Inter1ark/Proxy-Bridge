// Package control implements the localhost control socket. A client sends one
// text line: "stop <token>" asks the core to shut down, "watch <token>" keeps
// the connection open and streams status lines to it, "ping <token>" answers ok.
package control

import (
	"bufio"
	"crypto/subtle"
	"fmt"
	"io"
	"net"
	"strings"
	"sync"
	"time"

	"proxybridge/core/internal/status"
)

// Server is the control listener.
type Server struct {
	ln       net.Listener
	token    string
	reporter *status.Reporter
	onStop   func()
	stopOnce sync.Once
	wg       sync.WaitGroup
	mu       sync.Mutex
	conns    map[net.Conn]struct{}
}

// Listen binds the control socket on 127.0.0.1:port.
func Listen(port int, token string, reporter *status.Reporter, onStop func()) (*Server, error) {
	if token == "" {
		return nil, fmt.Errorf("control token must not be empty")
	}
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return nil, fmt.Errorf("control socket: %w", err)
	}
	s := &Server{ln: ln, token: token, reporter: reporter, onStop: onStop, conns: make(map[net.Conn]struct{})}
	s.wg.Add(1)
	go s.acceptLoop()
	return s, nil
}

// Addr returns the bound address.
func (s *Server) Addr() string { return s.ln.Addr().String() }

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
	line, err := bufio.NewReader(c).ReadString('\n')
	if err != nil && line == "" {
		return
	}
	fields := strings.Fields(line)
	if len(fields) != 2 || subtle.ConstantTimeCompare([]byte(fields[1]), []byte(s.token)) != 1 {
		io.WriteString(c, "error unauthorized\n")
		return
	}
	switch fields[0] {
	case "ping":
		io.WriteString(c, "ok\n")
	case "stop":
		io.WriteString(c, "ok\n")
		s.stopOnce.Do(func() { go s.onStop() })
	case "watch":
		_ = c.SetReadDeadline(time.Time{})
		s.reporter.Attach(c)
		// Block until the peer closes the connection.
		io.Copy(io.Discard, c)
		s.reporter.Detach(c)
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
