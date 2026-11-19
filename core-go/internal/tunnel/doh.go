//go:build darwin || linux

package tunnel

import (
	"bytes"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"sync"
	"time"

	"proxybridge/core/internal/proxy"
)

// dohEndpoint is a public DNS-over-HTTPS resolver reached by IP (no name lookup is
// needed to find it) with the TLS server name it expects.
type dohEndpoint struct {
	ip   string
	host string
}

// dohEndpoints are tried in order; the first one that answers through a proxy is
// kept for that proxy. Google answers through every proxy tested so far; Cloudflare
// is the fallback.
var dohEndpoints = []dohEndpoint{
	{"8.8.8.8", "dns.google"},
	{"8.8.4.4", "dns.google"},
	{"1.1.1.1", "cloudflare-dns.com"},
}

// openDNS443 takes plain DNS over TCP on port 443, the last resort through a proxy that
// refuses port 53 and the DoH endpoints.
var openDNS443 = netip.MustParseAddrPort("208.67.222.222:443")

// dohRetry is how long a DoH endpoint rests after dohFailLimit failures in a row.
const dohRetry = 2 * time.Minute

const (
	dohFailLimit = 3                       // failures in a row before an endpoint rests: one bad exit of a rotating proxy is not enough
	dohAttempt   = 4 * time.Second         // one try against one endpoint, so a stuck connection leaves time for the others
	dohHedge     = 1200 * time.Millisecond // no answer yet: ask the next endpoint too (another connection, another exit)
)

// proxyResolver answers DNS queries through one proxy. It prefers DNS over HTTPS on
// port 443 (one HTTP/2 connection carries all queries, so a page load does not open a
// connection through the proxy per name) and falls back to DNS over TCP to the resolver.
type proxyResolver struct {
	id     int
	dialer *proxy.Dialer
	log    Logger
	ask    func(ctx context.Context, ep dohEndpoint, query []byte) ([]byte, error) // p.doh; replaced in tests

	mu      sync.Mutex
	clients map[string]*http.Client // by endpoint ip
	fails   map[string]int          // endpoint ip -> failures in a row
	broken  map[string]time.Time    // endpoint ip -> when it was rested
	good    string                  // endpoint ip that answered last
	warned  time.Time
}

func newProxyResolver(id int, d *proxy.Dialer, log Logger) *proxyResolver {
	p := &proxyResolver{id: id, dialer: d, log: log, clients: make(map[string]*http.Client),
		fails: make(map[string]int), broken: make(map[string]time.Time)}
	p.ask = p.doh
	return p
}

// Exchange sends one DNS query through the proxy and returns the answer.
func (p *proxyResolver) Exchange(ctx context.Context, resolver netip.AddrPort, query []byte) ([]byte, error) {
	answer, err := p.race(ctx, p.candidates(), query)
	if err == nil {
		return answer, nil
	}
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	answer, err = dnsOverTCP(ctx, p.dialer, resolver, query)
	if err != nil && ctx.Err() == nil {
		// Proxies that allow only web ports: OpenDNS answers plain DNS over TCP on 443 too.
		answer, err = dnsOverTCP(ctx, p.dialer, openDNS443, query)
	}
	if err != nil {
		p.mu.Lock()
		warn := time.Since(p.warned) > 30*time.Second
		if warn {
			p.warned = time.Now()
		}
		p.mu.Unlock()
		if warn {
			p.log.Warnf("dns via proxy %d: no DNS over HTTPS endpoint answered and neither %s nor OpenDNS on port 443 answers over TCP (%v); names will not resolve", p.id, resolver, err)
		}
		return nil, err
	}
	return answer, nil
}

type dohResult struct {
	ep     dohEndpoint
	answer []byte
	err    error
}

// race asks the endpoints in order, starting the next one when the previous fails or
// stays silent for dohHedge; the first answer wins and the slower tries are cancelled.
func (p *proxyResolver) race(ctx context.Context, eps []dohEndpoint, query []byte) ([]byte, error) {
	if len(eps) == 0 {
		return nil, errors.New("every DNS over HTTPS endpoint is resting")
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	results := make(chan dohResult, len(eps))
	next, running := 0, 0
	launch := func() {
		ep := eps[next]
		next++
		running++
		go func() {
			actx, acancel := context.WithTimeout(ctx, dohAttempt)
			defer acancel()
			answer, err := p.ask(actx, ep, query)
			results <- dohResult{ep, answer, err}
		}()
	}
	launch()
	hedge := time.NewTimer(dohHedge)
	defer hedge.Stop()
	var lastErr error
	for running > 0 {
		select {
		case r := <-results:
			running--
			if r.err == nil {
				p.noteOK(r.ep)
				return r.answer, nil
			}
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			p.noteFail(r.ep, r.err)
			lastErr = r.err
			if next < len(eps) {
				launch()
				hedge.Reset(dohHedge)
			}
		case <-hedge.C:
			if next < len(eps) {
				launch()
				hedge.Reset(dohHedge)
			}
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	return nil, lastErr
}

func (p *proxyResolver) noteOK(ep dohEndpoint) {
	p.mu.Lock()
	p.good = ep.ip
	p.fails[ep.ip] = 0
	p.mu.Unlock()
}

func (p *proxyResolver) noteFail(ep dohEndpoint, err error) {
	p.mu.Lock()
	p.fails[ep.ip]++
	rest := p.fails[ep.ip] >= dohFailLimit
	if rest {
		p.fails[ep.ip] = 0
		p.broken[ep.ip] = time.Now()
		if p.good == ep.ip {
			p.good = ""
		}
	}
	p.mu.Unlock()
	if rest {
		p.log.Warnf("dns via proxy %d: DNS over HTTPS via %s (%s) failed %d times in a row, resting it for %s: %v", p.id, ep.host, ep.ip, dohFailLimit, dohRetry, err)
	} else {
		p.log.Debugf("dns via proxy %d: DNS over HTTPS via %s (%s) failed: %v", p.id, ep.host, ep.ip, err)
	}
}

// candidates lists the endpoints worth trying now: the one that worked last first,
// then the rest that are not resting.
func (p *proxyResolver) candidates() []dohEndpoint {
	p.mu.Lock()
	defer p.mu.Unlock()
	var out []dohEndpoint
	for _, ep := range dohEndpoints {
		if ep.ip == p.good {
			out = append([]dohEndpoint{ep}, out...)
			continue
		}
		if t, rested := p.broken[ep.ip]; rested && time.Since(t) < dohRetry {
			continue
		}
		out = append(out, ep)
	}
	return out
}

func (p *proxyResolver) client(ep dohEndpoint) *http.Client {
	p.mu.Lock()
	defer p.mu.Unlock()
	if c, ok := p.clients[ep.ip]; ok {
		return c
	}
	dialer := p.dialer
	target := net.JoinHostPort(ep.ip, "443")
	tr := &http.Transport{
		DialContext: func(ctx context.Context, network, addr string) (net.Conn, error) {
			return dialer.Dial(ctx, target)
		},
		TLSClientConfig: &tls.Config{ServerName: ep.host, MinVersion: tls.VersionTLS12},
		// HTTP/2 inside TLS (the proxy sees only a tunnel): every query of a page load shares
		// one connection, which with a rotating proxy also means one exit instead of dozens.
		ForceAttemptHTTP2: true,
		HTTP2: &http.HTTP2Config{
			SendPingTimeout: 15 * time.Second, // ping a quiet connection; a dead exit is dropped
			PingTimeout:     5 * time.Second,
		},
		MaxIdleConns:        8,
		MaxIdleConnsPerHost: 4, // HTTP/1.1 fallback: keep a few warm instead of the default 2
		IdleConnTimeout:     90 * time.Second,
		TLSHandshakeTimeout: 6 * time.Second,
	}
	c := &http.Client{Transport: tr}
	p.clients[ep.ip] = c
	return c
}

func (p *proxyResolver) doh(ctx context.Context, ep dohEndpoint, query []byte) ([]byte, error) {
	if len(query) < 12 {
		return nil, errors.New("short query")
	}
	// RFC 8484: the id is 0 on the wire so answers are cacheable; it is restored below.
	q := make([]byte, len(query))
	copy(q, query)
	q[0], q[1] = 0, 0
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://"+ep.host+"/dns-query", bytes.NewReader(q))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/dns-message")
	req.Header.Set("Accept", "application/dns-message")
	resp, err := p.client(ep).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
		return nil, fmt.Errorf("http %d", resp.StatusCode)
	}
	answer, err := io.ReadAll(io.LimitReader(resp.Body, 65535))
	if err != nil {
		return nil, err
	}
	if len(answer) < 12 {
		return nil, errors.New("short answer")
	}
	answer[0], answer[1] = query[0], query[1]
	return answer, nil
}

// Close drops idle HTTPS connections.
func (p *proxyResolver) Close() {
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, c := range p.clients {
		if t, ok := c.Transport.(*http.Transport); ok {
			t.CloseIdleConnections()
		}
	}
}
