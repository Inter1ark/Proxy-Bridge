// Package check tests a proxy the way the desktop checker does: one HTTP request to
// ip-api.com and ipinfo.io through it and, for SOCKS5, a STUN request through UDP ASSOCIATE.
package check

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"time"

	"proxybridge/core/internal/proxy"
)

// Result is what Proxy returns (also marshalled as JSON for the mobile bindings).
type Result struct {
	OK          bool   `json:"ok"`
	Error       string `json:"error,omitempty"`
	IP          string `json:"ip,omitempty"`
	Country     string `json:"country,omitempty"`
	CountryCode string `json:"country_code,omitempty"`
	City        string `json:"city,omitempty"`
	LatencyMs   int    `json:"ms"`
	UDP         bool   `json:"udp"`
	UDPIP       string `json:"udp_ip,omitempty"`
}

var stunServers = []struct {
	host string
	port uint16
}{{"stun.l.google.com", 19302}, {"stun.cloudflare.com", 3478}, {"stun1.l.google.com", 19302}}

// Proxy checks rawURL; lang is "ru" or "en" (country names).
func Proxy(rawURL, lang string) Result {
	cfg, err := proxy.Parse(rawURL)
	if err != nil {
		return Result{Error: err.Error()}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip4", cfg.Host)
	if err != nil || len(ips) == 0 {
		return Result{Error: "cannot resolve " + cfg.Host}
	}
	d := proxy.NewDialer(cfg, net.JoinHostPort(ips[0].Unmap().String(), fmt.Sprint(cfg.Port)), 10*time.Second)

	if lang != "en" {
		lang = "ru"
	}
	newClient := func(timeout time.Duration) *http.Client {
		return &http.Client{Transport: &http.Transport{
			DialContext:       func(ctx context.Context, network, addr string) (net.Conn, error) { return d.Dial(ctx, addr) },
			DisableKeepAlives: true,
		}, Timeout: timeout}
	}
	// Two independent services at once. ip-api.com silently drops requests from exits it has banned,
	// so alone it used to report a working proxy as dead after its whole timeout.
	type answer struct {
		info    exitInfo
		ms      int
		err     string
		primary bool
	}
	answers := make(chan answer, 2)
	go func() {
		info, ms, e := askIPAPI(newClient(12*time.Second), lang)
		answers <- answer{info, ms, e, true}
	}()
	go func() {
		info, ms, e := askIPInfo(newClient(12 * time.Second))
		answers <- answer{info, ms, e, false}
	}()
	first := <-answers
	best := first
	if first.err != "" {
		second := <-answers
		// Both failed: report what ip-api said, it is the more telling of the two.
		if second.err == "" || second.primary {
			best = second
		}
	} else if !first.primary {
		// The fallback answered first: ip-api gets a short grace, its country names are localised.
		select {
		case second := <-answers:
			if second.err == "" {
				second.ms = min(second.ms, first.ms)
				best = second
			}
		case <-time.After(2 * time.Second):
		}
	}
	if best.err != "" {
		return Result{Error: best.err}
	}
	info, ms := best.info, best.ms
	res := Result{OK: true, IP: info.Query, Country: info.Country, CountryCode: info.CountryCode, City: info.City, LatencyMs: ms}
	if cfg.Kind == proxy.KindSOCKS5 {
		res.UDP, res.UDPIP = probeUDP(ctx, d)
	}
	return res
}

// exitInfo is what an address service says about the proxy's exit.
type exitInfo struct {
	Status, Message, Country, CountryCode, City, Query string
}

func fetchJSON(client *http.Client, url string, out any) (int, string) {
	started := time.Now()
	resp, err := client.Get(url)
	if err != nil {
		return 0, shortErr(err)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<16))
	resp.Body.Close()
	if err != nil {
		return 0, shortErr(err)
	}
	ms := int(time.Since(started).Milliseconds())
	if err := json.Unmarshal(body, out); err != nil {
		return ms, "bad answer"
	}
	return ms, ""
}

func askIPAPI(client *http.Client, lang string) (exitInfo, int, string) {
	var info exitInfo
	ms, e := fetchJSON(client, "http://ip-api.com/json/?fields=status,message,country,countryCode,city,query&lang="+lang, &info)
	if e == "" && info.Status != "success" {
		e = "ip-api: bad answer"
		if info.Message != "" {
			e = "ip-api: " + info.Message
		}
	}
	return info, ms, e
}

func askIPInfo(client *http.Client) (exitInfo, int, string) {
	var j struct{ IP, City, Country string }
	ms, e := fetchJSON(client, "https://ipinfo.io/json", &j)
	if e == "" && j.IP == "" {
		e = "ipinfo: bad answer"
	}
	return exitInfo{Status: "success", Query: j.IP, City: j.City, Country: j.Country, CountryCode: j.Country}, ms, e
}

// JSON is Proxy marshalled for the bindings.
func JSON(rawURL, lang string) string {
	b, _ := json.Marshal(Proxy(rawURL, lang))
	return string(b)
}

// probeUDP opens UDP ASSOCIATE and sends a STUN Binding Request; the mapped address is the
// proxy's UDP exit. Proxies that refuse the association report no UDP.
func probeUDP(ctx context.Context, d *proxy.Dialer) (bool, string) {
	a, err := d.UDPAssociate(ctx)
	if err != nil {
		return false, ""
	}
	defer a.Close()
	req := make([]byte, 20)
	req[1] = 1
	copy(req[4:8], []byte{0x21, 0x12, 0xA4, 0x42})
	now := time.Now().UnixNano()
	for i := 8; i < 20; i++ {
		req[i] = byte(now >> (uint(i%8) * 8))
	}
	for _, srv := range stunServers {
		ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip4", srv.host)
		if err != nil || len(ips) == 0 {
			continue
		}
		dst := netip.AddrPortFrom(ips[0].Unmap(), srv.port)
		if err := a.WriteTo(req, dst); err != nil {
			continue
		}
		buf := make([]byte, 1500)
		n, _, err := a.ReadFrom(buf, time.Now().Add(4*time.Second))
		if err != nil || n < 20 {
			continue
		}
		if ip := stunMappedIP(buf[:n]); ip != "" {
			return true, ip
		}
	}
	return false, ""
}

func stunMappedIP(msg []byte) string {
	off := 20
	for off+4 <= len(msg) {
		at := int(msg[off])<<8 | int(msg[off+1])
		al := int(msg[off+2])<<8 | int(msg[off+3])
		if at == 0x20 && al >= 8 && off+4+al <= len(msg) {
			v := msg[off+4 : off+4+al]
			magic := []byte{0x21, 0x12, 0xA4, 0x42}
			return fmt.Sprintf("%d.%d.%d.%d", v[4]^magic[0], v[5]^magic[1], v[6]^magic[2], v[7]^magic[3])
		}
		off += 4 + (al+3)&^3
	}
	return ""
}

func shortErr(err error) string {
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return "timeout"
	}
	return err.Error()
}
