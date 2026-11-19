//go:build darwin || linux

package tunnel

import (
	"context"
	"encoding/binary"
	"net"
	"net/netip"
	"os"
	"sort"
	"strconv"
	"sync"
	"testing"
	"time"

	"proxybridge/core/internal/proxy"
)

func dnsQuery(id uint16, name string, qtype uint16) []byte {
	q := make([]byte, 12)
	binary.BigEndian.PutUint16(q[0:], id)
	q[2] = 1 // RD
	binary.BigEndian.PutUint16(q[4:], 1)
	start := 0
	for i := 0; i <= len(name); i++ {
		if i == len(name) || name[i] == '.' {
			q = append(q, byte(i-start))
			q = append(q, name[start:i]...)
			start = i + 1
		}
	}
	q = append(q, 0, byte(qtype>>8), byte(qtype), 0, 1)
	return q
}

// PB_LIVE_PROXY=socks5://user:pass@host:port go test -run Live -v
func TestLiveDoHBurst(t *testing.T) {
	raw := os.Getenv("PB_LIVE_PROXY")
	if raw == "" {
		t.Skip("PB_LIVE_PROXY not set")
	}
	cfg, err := proxy.Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	ips, err := net.LookupHost(cfg.Host)
	if err != nil {
		t.Fatal(err)
	}
	d := proxy.NewDialer(cfg, net.JoinHostPort(ips[0], strconv.Itoa(cfg.Port)), 10*time.Second)
	res := newProxyResolver(1, d, liveLog{})
	names := []string{"google.com", "youtube.com", "yandex.ru", "github.com", "wikipedia.org", "apple.com", "icloud.com",
		"cloudflare.com", "openai.com", "chatgpt.com", "telegram.org", "mozilla.org", "amazon.com", "microsoft.com",
		"bing.com", "reddit.com", "x.com", "instagram.com", "facebook.com", "netflix.com"}
	resolver := netip.MustParseAddrPort("8.8.8.8:53")
	for round := 1; round <= 3; round++ {
		var mu sync.Mutex
		var lat []time.Duration
		fails := 0
		var wg sync.WaitGroup
		start := time.Now()
		for i, n := range names {
			for _, qt := range []uint16{1, 28} {
				wg.Add(1)
				go func(id uint16, n string, qt uint16) {
					defer wg.Done()
					ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
					defer cancel()
					t0 := time.Now()
					_, err := res.Exchange(ctx, resolver, dnsQuery(id, n, qt))
					mu.Lock()
					if err != nil {
						fails++
					} else {
						lat = append(lat, time.Since(t0))
					}
					mu.Unlock()
				}(uint16(i*2+int(qt)), n, qt)
			}
		}
		wg.Wait()
		sort.Slice(lat, func(i, j int) bool { return lat[i] < lat[j] })
		p50, p90 := time.Duration(0), time.Duration(0)
		if len(lat) > 0 {
			p50, p90 = lat[len(lat)/2], lat[len(lat)*9/10]
		}
		t.Logf("round %d: %d queries, %d failed, total %v, p50 %v, p90 %v", round, len(names)*2, fails, time.Since(start).Round(time.Millisecond), p50.Round(time.Millisecond), p90.Round(time.Millisecond))
		time.Sleep(500 * time.Millisecond)
	}
}

type liveLog struct{}

func (liveLog) Debugf(string, ...any) {}
func (liveLog) Infof(string, ...any)  {}
func (liveLog) Warnf(string, ...any)  {}
func (liveLog) Errorf(string, ...any) {}
