//go:build darwin || linux

package tunnel

import (
	"context"
	"errors"
	"net/netip"
	"sync"
	"testing"
	"time"
)

type fakeExchanger struct {
	mu    sync.Mutex
	calls int
	err   error
}

func (f *fakeExchanger) Exchange(ctx context.Context, resolver netip.AddrPort, query []byte) ([]byte, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	if f.err != nil {
		return nil, f.err
	}
	return []byte("via proxy"), nil
}

func (f *fakeExchanger) set(err error) {
	f.mu.Lock()
	f.err = err
	f.mu.Unlock()
}

func TestDNSFallbackKeepsDirectProgramsOnline(t *testing.T) {
	res := &fakeExchanger{}
	asked := 0
	now := time.Unix(1000, 0)
	fb := newDNSFallback(1, quietLog{}, func(ctx context.Context, q []byte) ([]byte, error) {
		asked++
		return []byte("regular"), nil
	})
	fb.now = func() time.Time { return now }
	resolver := netip.MustParseAddrPort("1.1.1.1:53")
	q := make([]byte, 20)

	// the proxy works: nothing leaves around it
	if a, err := exchangeDNS(res, fb, resolver, q); err != nil || string(a) != "via proxy" || asked != 0 {
		t.Fatalf("working proxy: answer %q err %v, fallback asked %d times", a, err, asked)
	}

	// the proxy dies: this lookup and the next ones are answered by the regular DNS
	res.set(errors.New("connection refused"))
	if a, err := exchangeDNS(res, fb, resolver, q); err != nil || string(a) != "regular" {
		t.Fatalf("dead proxy: answer %q err %v", a, err)
	}
	before := res.calls
	for i := 0; i < 5; i++ {
		if a, _ := exchangeDNS(res, fb, resolver, q); string(a) != "regular" {
			t.Fatalf("lookup %d during the outage: %q", i, a)
		}
	}
	if res.calls != before {
		t.Fatalf("the dead proxy was asked %d more times during the rest period", res.calls-before)
	}

	// after the rest period one lookup tries the proxy again, and it is still dead
	now = now.Add(dnsDownFor + time.Second)
	if a, _ := exchangeDNS(res, fb, resolver, q); string(a) != "regular" || res.calls != before+1 {
		t.Fatalf("probe: answer %q, proxy asked %d times (want 1)", a, res.calls-before)
	}

	// the proxy is back: lookups return to it
	res.set(nil)
	now = now.Add(dnsDownFor + time.Second)
	if a, _ := exchangeDNS(res, fb, resolver, q); string(a) != "via proxy" {
		t.Fatalf("recovered proxy: %q", a)
	}
	asked = 0
	if a, _ := exchangeDNS(res, fb, resolver, q); string(a) != "via proxy" || asked != 0 {
		t.Fatalf("after recovery: %q, fallback asked %d times", a, asked)
	}
}

func TestDNSWithoutFallbackNeverLeavesTheProxy(t *testing.T) {
	res := &fakeExchanger{err: errors.New("connection refused")}
	if _, err := exchangeDNS(res, nil, netip.MustParseAddrPort("1.1.1.1:53"), make([]byte, 20)); err == nil {
		t.Fatal("a dead proxy with no fallback must fail the lookup")
	}
}

func TestOnlyOneLookupProbesADeadProxy(t *testing.T) {
	fb := newDNSFallback(1, quietLog{}, nil)
	now := time.Unix(1000, 0)
	fb.now = func() time.Time { return now }
	fb.proxyFailed(errors.New("x"))
	now = now.Add(dnsDownFor + time.Second)
	if !fb.useProxy() {
		t.Fatal("the first lookup after the rest period must probe the proxy")
	}
	if fb.useProxy() {
		t.Fatal("a second lookup must not probe while the first is still out")
	}
}

type fakeUDP struct {
	answer []byte
	delay  time.Duration
	err    error
}

func (c *fakeUDP) Write(b []byte) (int, error) { return len(b), nil }
func (c *fakeUDP) Read(b []byte) (int, error) {
	time.Sleep(c.delay)
	if c.err != nil {
		return 0, c.err
	}
	return copy(b, c.answer), nil
}
func (c *fakeUDP) SetDeadline(time.Time) error { return nil }
func (c *fakeUDP) Close() error                { return nil }

func dnsAnswer(id byte, rcode byte) []byte {
	a := make([]byte, 16)
	a[0], a[1] = 0, id
	a[2], a[3] = 0x81, 0x80|rcode
	return a
}

func TestAskResolversPrefersARealAnswerOverRefused(t *testing.T) {
	query := make([]byte, 16)
	query[1] = 7
	conns := map[string]*fakeUDP{
		"192.168.0.1:53": {answer: dnsAnswer(7, 5)},                               // REFUSED, instantly
		"1.1.1.1:53":     {answer: dnsAnswer(7, 0), delay: 30 * time.Millisecond}, // NOERROR, later
	}
	dial := func(ctx context.Context, addr string) (udpConn, error) { return conns[addr], nil }
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	a, err := askResolvers(ctx, []netip.AddrPort{netip.MustParseAddrPort("192.168.0.1:53"), netip.MustParseAddrPort("1.1.1.1:53")}, query, dial)
	if err != nil || a[3]&0x0F != 0 {
		t.Fatalf("answer %v err %v: want the NOERROR answer", a, err)
	}
}

func TestAskResolversSurvivesADeadResolver(t *testing.T) {
	query := make([]byte, 16)
	query[1] = 9
	conns := map[string]*fakeUDP{
		"1.1.1.1:53":     {err: errors.New("i/o timeout"), delay: 20 * time.Millisecond},
		"192.168.0.1:53": {answer: dnsAnswer(9, 0), delay: 40 * time.Millisecond},
	}
	dial := func(ctx context.Context, addr string) (udpConn, error) { return conns[addr], nil }
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	a, err := askResolvers(ctx, []netip.AddrPort{netip.MustParseAddrPort("1.1.1.1:53"), netip.MustParseAddrPort("192.168.0.1:53")}, query, dial)
	if err != nil || len(a) == 0 {
		t.Fatalf("answer %v err %v", a, err)
	}
}

func TestVPNClientDetection(t *testing.T) {
	yes := []string{
		"/private/var/folders/tq/x/X/F906/d/Wrapper/Runner.app/PlugIns/PacketTunnel.appex/PacketTunnel",
		"/Applications/AmneziaVPN.app/Contents/MacOS/AmneziaVPN",
		"/Applications/V2Box.app/Contents/MacOS/V2Box",
		"/opt/homebrew/bin/wireguard-go",
		"/Library/SystemExtensions/ABC/com.example.tunnel.systemextension/Contents/MacOS/ext",
	}
	no := []string{
		"/Applications/Telegram.app/Contents/MacOS/Telegram",
		"/Applications/Safari.app/Contents/MacOS/Safari",
		"/Applications/WhatsApp.app/Contents/MacOS/WhatsApp",
		"/Applications/OmniOutliner.app/Contents/MacOS/OmniOutliner",
		"/System/Library/Frameworks/WebKit.framework/Versions/A/XPCServices/com.apple.WebKit.Networking.xpc/Contents/MacOS/com.apple.WebKit.Networking",
		"",
	}
	for _, exe := range yes {
		if !isVPNClient(exe) {
			t.Errorf("%q must be taken for a VPN client", exe)
		}
	}
	for _, exe := range no {
		if isVPNClient(exe) {
			t.Errorf("%q must not be taken for a VPN client", exe)
		}
	}
}
