//go:build darwin || linux

package tunnel

import (
	"context"
	"errors"
	"net/netip"
	"sync"
	"time"
)

// With "DNS via proxy" every name lookup of the computer goes through a proxy: the lookups
// come from the system resolver, not from the programs, so they cannot be told apart. When
// that proxy cannot be reached, nothing resolves and programs that have no rule lose the
// internet together with the proxied ones. dnsFallback keeps them online: while the proxy
// does not answer, lookups are answered by the regular resolvers outside the tunnel.
//
// It exists only when "all other programs" go direct. Those programs talk to the network
// from the real address anyway; a proxied program still connects only through its proxy,
// so what leaves around the proxy during an outage is the name it asked for, never its
// traffic. When everything goes through a proxy there is no fallback.
type dnsFallback struct {
	id  int
	log Logger
	ask func(ctx context.Context, query []byte) ([]byte, error)
	now func() time.Time

	mu      sync.Mutex
	down    bool
	retryAt time.Time
	probing bool
}

const (
	// dnsProxyBudget bounds one lookup through the proxy when a fallback exists, so the
	// first lookup of an outage is answered before the system resolver gives up.
	dnsProxyBudget = 5 * time.Second
	// dnsDownFor is how long the proxy is left alone after a failed lookup. Then one lookup
	// tries it again while the others keep using the fallback.
	dnsDownFor = 45 * time.Second
	// dnsFallbackBudget bounds one lookup through the regular resolvers.
	dnsFallbackBudget = 4 * time.Second
)

// exchanger answers one DNS query through a proxy (*proxyResolver).
type exchanger interface {
	Exchange(ctx context.Context, resolver netip.AddrPort, query []byte) ([]byte, error)
}

func newDNSFallback(id int, log Logger, ask func(ctx context.Context, query []byte) ([]byte, error)) *dnsFallback {
	return &dnsFallback{id: id, log: log, ask: ask, now: time.Now}
}

// useProxy tells whether this lookup should go through the proxy: always while it works,
// and one lookup at a time once dnsDownFor has passed since the last failure.
func (f *dnsFallback) useProxy() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	if !f.down {
		return true
	}
	if f.probing || f.now().Before(f.retryAt) {
		return false
	}
	f.probing = true
	return true
}

func (f *dnsFallback) proxyOK() {
	f.mu.Lock()
	was := f.down
	f.down, f.probing = false, false
	f.mu.Unlock()
	if was {
		f.log.Infof("dns via proxy %d answers again: name lookups go through the proxy", f.id)
	}
}

func (f *dnsFallback) proxyFailed(err error) {
	f.mu.Lock()
	was := f.down
	f.down, f.probing = true, false
	f.retryAt = f.now().Add(dnsDownFor)
	f.mu.Unlock()
	if !was {
		f.log.Warnf("dns via proxy %d does not answer (%v): the proxy cannot be reached from this network. Name lookups are answered by the regular DNS until it is back, so programs without a rule stay online; programs with a rule have no connection", f.id, err)
	}
}

// exchangeDNS answers one query: through the proxy, or through the fallback while the
// proxy is down. fb may be nil (no fallback: the proxy or nothing).
func exchangeDNS(res exchanger, fb *dnsFallback, resolver netip.AddrPort, query []byte) ([]byte, error) {
	if fb == nil {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		return res.Exchange(ctx, resolver, query)
	}
	if fb.useProxy() {
		ctx, cancel := context.WithTimeout(context.Background(), dnsProxyBudget)
		answer, err := res.Exchange(ctx, resolver, query)
		cancel()
		if err == nil {
			fb.proxyOK()
			return answer, nil
		}
		fb.proxyFailed(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), dnsFallbackBudget)
	defer cancel()
	return fb.ask(ctx, query)
}

// askResolvers sends the query over UDP to every resolver at once and returns the first
// usable answer. A resolver that refuses (a router without a DNS server answers REFUSED)
// loses to one that answers properly.
func askResolvers(ctx context.Context, resolvers []netip.AddrPort, query []byte, dial func(ctx context.Context, addr string) (udpConn, error)) ([]byte, error) {
	if len(resolvers) == 0 {
		return nil, errors.New("no regular DNS resolver is known")
	}
	if len(query) < 12 {
		return nil, errors.New("short query")
	}
	type result struct {
		answer []byte
		err    error
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	results := make(chan result, len(resolvers))
	for _, rs := range resolvers {
		go func(addr string) {
			c, err := dial(ctx, addr)
			if err != nil {
				results <- result{nil, err}
				return
			}
			defer c.Close()
			deadline, ok := ctx.Deadline()
			if !ok {
				deadline = time.Now().Add(dnsFallbackBudget)
			}
			_ = c.SetDeadline(deadline)
			if _, err := c.Write(query); err != nil {
				results <- result{nil, err}
				return
			}
			buf := make([]byte, 4096)
			for {
				n, err := c.Read(buf)
				if err != nil {
					results <- result{nil, err}
					return
				}
				if n >= 12 && buf[0] == query[0] && buf[1] == query[1] {
					results <- result{append([]byte(nil), buf[:n]...), nil}
					return
				}
			}
		}(rs.String())
	}
	var spare []byte
	var lastErr error
	for range resolvers {
		select {
		case r := <-results:
			if r.err != nil {
				lastErr = r.err
				continue
			}
			if rcode := r.answer[3] & 0x0F; rcode == 0 || rcode == 3 { // NOERROR, NXDOMAIN
				return r.answer, nil
			}
			spare = r.answer
		case <-ctx.Done():
			if spare != nil {
				return spare, nil
			}
			return nil, ctx.Err()
		}
	}
	if spare != nil {
		return spare, nil
	}
	return nil, lastErr
}

// udpConn is what askResolvers needs from a connected UDP socket.
type udpConn interface {
	Write([]byte) (int, error)
	Read([]byte) (int, error)
	SetDeadline(time.Time) error
	Close() error
}
