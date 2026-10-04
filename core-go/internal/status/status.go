// Package status prints JSON status lines and fans them out to watchers
// connected over the control socket.
package status

import (
	"encoding/json"
	"io"
	"os"
	"sync"
)

// Line is one status event.
type Line struct {
	Status  string `json:"status"`
	Tun     string `json:"tun,omitempty"`
	Proxy   string `json:"proxy,omitempty"`
	Message string `json:"message,omitempty"`
	Up      *int64 `json:"up,omitempty"`
	Down    *int64 `json:"down,omitempty"`
}

// Reporter writes every line to stdout and to every attached watcher.
type Reporter struct {
	mu       sync.Mutex
	out      io.Writer
	last     []byte
	watchers map[io.Writer]struct{}
}

// New creates a reporter that writes to stdout.
func New() *Reporter {
	return &Reporter{out: os.Stdout, watchers: make(map[io.Writer]struct{})}
}

// Emit publishes one line.
func (r *Reporter) Emit(l Line) {
	b, err := json.Marshal(l)
	if err != nil {
		return
	}
	b = append(b, '\n')
	r.mu.Lock()
	defer r.mu.Unlock()
	if l.Status != "stats" {
		r.last = b
	}
	r.out.Write(b)
	for w := range r.watchers {
		if _, err := w.Write(b); err != nil {
			delete(r.watchers, w)
		}
	}
}

// Up reports that the tunnel is ready.
func (r *Reporter) Up(tun, proxy string) {
	r.Emit(Line{Status: "up", Tun: tun, Proxy: proxy})
}

// Error reports an error.
func (r *Reporter) Error(msg string) {
	r.Emit(Line{Status: "error", Message: msg})
}

// Stats reports traffic counters.
func (r *Reporter) Stats(up, down int64) {
	r.Emit(Line{Status: "stats", Up: &up, Down: &down})
}

// Attach adds a watcher. The last non-stats line is replayed to it first so a
// late watcher learns the current state immediately.
func (r *Reporter) Attach(w io.Writer) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.last != nil {
		if _, err := w.Write(r.last); err != nil {
			return
		}
	}
	r.watchers[w] = struct{}{}
}

// Detach removes a watcher.
func (r *Reporter) Detach(w io.Writer) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.watchers, w)
}
