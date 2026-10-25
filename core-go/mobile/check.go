//go:build linux

package pbmobile

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

// CheckResult is what CheckProxy returns (as JSON).
type CheckResult struct {
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

var stunServers = []string{"stun.l.google.com:19302", "stun.cloudflare.com:3478", "stun1.l.google.com:19302"}

// CheckProxy connects through the proxy to ip-api.com (same check as on the desktop) and,
// for SOCKS5, asks for UDP ASSOCIATE and sends a STUN request through it. lang is "ru" or "en".
func CheckProxy(rawURL, lang string) string {
	res := checkProxy(rawURL, lang)
	b, _ := json.Marshal(res)
	return string(b)
}

func checkProxy(rawURL, lang string) CheckResult {
	cfg, err := proxy.Parse(rawURL)
	if err != nil {
		return CheckResult{Error: err.Error()}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip4", cfg.Host)
	if err != nil || len(ips) == 0 {
		return CheckResult{Error: "cannot resolve " + cfg.Host}
	}
	d := proxy.NewDialer(cfg, net.JoinHostPort(ips[0].Unmap().String(), fmt.Sprint(cfg.Port)), 10*time.Second)

	if lang != "en" {
		lang = "ru"
	}
	client := &http.Client{Transport: &http.Transport{
		DialContext:       func(ctx context.Context, network, addr string) (net.Conn, error) { return d.Dial(ctx, addr) },
		DisableKeepAlives: true,
	}, Timeout: 12 * time.Second}
	started := time.Now()
	resp, err := client.Get("http://ip-api.com/json/?fields=status,message,country,countryCode,city,query&lang=" + lang)
	if err != nil {
		return CheckResult{Error: shortErr(err)}
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<16))
	resp.Body.Close()
	if err != nil {
		return CheckResult{Error: shortErr(err)}
	}
	ms := int(time.Since(started).Milliseconds())
	var info struct {
		Status, Message, Country, CountryCode, City, Query string
	}
	if err := json.Unmarshal(body, &info); err != nil || info.Status != "success" {
		return CheckResult{Error: "ip-api: " + firstNonEmpty(info.Message, "bad answer")}
	}
	res := CheckResult{OK: true, IP: info.Query, Country: info.Country, CountryCode: info.CountryCode, City: info.City, LatencyMs: ms}
	if cfg.Kind == proxy.KindSOCKS5 {
		res.UDP, res.UDPIP = probeUDP(ctx, d)
	}
	return res
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
	for i := 8; i < 20; i++ {
		req[i] = byte(time.Now().UnixNano() >> (uint(i) % 8 * 8))
	}
	for _, srv := range stunServers {
		ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip4", srv[:len(srv)-6])
		if err != nil || len(ips) == 0 {
			continue
		}
		port := 19302
		if srv == "stun.cloudflare.com:3478" {
			port = 3478
		}
		dst := netip.AddrPortFrom(ips[0].Unmap(), uint16(port))
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

func firstNonEmpty(a, b string) string {
	if a != "" {
		return a
	}
	return b
}
