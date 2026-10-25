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
// kept for that proxy. Google answers plain HTTP/1.1 POSTs through every proxy
// tested so far; Cloudflare is the fallback.
var dohEndpoints = []dohEndpoint{
	{"8.8.8.8", "dns.google"},
	{"8.8.4.4", "dns.google"},
	{"1.1.1.1", "cloudflare-dns.com"},
}

// dohRetry is how long a failed DoH endpoint stays switched off for a proxy.
const dohRetry = 2 * time.Minute

// proxyResolver answers DNS queries through one proxy. It prefers DNS over HTTPS on
// port 443 (several proxy vendors accept a CONNECT to port 53 and then drop every
// byte, which looks like a resolver that never answers) and falls back to DNS over
// TCP on port 53 of the requested resolver when no DoH endpoint works.
type proxyResolver struct {
	id     int
	dialer *proxy.Dialer
	log    Logger

	mu       sync.Mutex
	clients  map[string]*http.Client // by endpoint ip
	broken   map[string]time.Time    // endpoint ip -> when it last failed
	good     string                  // endpoint ip that answered last
	warnedAt time.Time
}

func newProxyResolver(id int, d *proxy.Dialer, log Logger) *proxyResolver {
	return &proxyResolver{id: id, dialer: d, log: log, clients: make(map[string]*http.Client), broken: make(map[string]time.Time)}
}

// Exchange sends one DNS query through the proxy and returns the answer.
func (p *proxyResolver) Exchange(ctx context.Context, resolver netip.AddrPort, query []byte) ([]byte, error) {
	for _, ep := range p.candidates() {
		answer, err := p.doh(ctx, ep, query)
		if err == nil {
			p.mu.Lock()
			p.good = ep.ip
			p.mu.Unlock()
			return answer, nil
		}
		p.mu.Lock()
		p.broken[ep.ip] = time.Now()
		if p.good == ep.ip {
			p.good = ""
		}
		p.mu.Unlock()
		p.log.Warnf("dns via proxy %d: DNS over HTTPS via %s (%s) failed: %v", p.id, ep.host, ep.ip, err)
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
	}
	answer, err := dnsOverTCP(ctx, p.dialer, resolver, query)
	if err != nil {
		p.mu.Lock()
		warn := time.Since(p.warnedAt) > 30*time.Second
		if warn {
			p.warnedAt = time.Now()
		}
		p.mu.Unlock()
		if warn {
			p.log.Warnf("dns via proxy %d: no DNS over HTTPS endpoint works and %s does not answer over TCP (%v); names will not resolve", p.id, resolver, err)
		}
		return nil, err
	}
	return answer, nil
}

// candidates lists the endpoints worth trying now: the one that worked last first,
// then the rest that are not resting after a failure.
func (p *proxyResolver) candidates() []dohEndpoint {
	p.mu.Lock()
	defer p.mu.Unlock()
	var out []dohEndpoint
	for _, ep := range dohEndpoints {
		if ep.ip == p.good {
			out = append([]dohEndpoint{ep}, out...)
			continue
		}
		if t, failed := p.broken[ep.ip]; failed && time.Since(t) < dohRetry {
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
		TLSClientConfig:     &tls.Config{ServerName: ep.host, MinVersion: tls.VersionTLS12},
		TLSNextProto:        map[string]func(string, *tls.Conn) http.RoundTripper{}, // HTTP/1.1 only: fewer surprises behind proxies
		MaxIdleConns:        4,
		IdleConnTimeout:     90 * time.Second,
		TLSHandshakeTimeout: 10 * time.Second,
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
