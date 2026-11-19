//go:build darwin || linux

package tunnel

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"
)

type quietLog struct{}

func (quietLog) Debugf(string, ...any) {}
func (quietLog) Infof(string, ...any)  {}
func (quietLog) Warnf(string, ...any)  {}
func (quietLog) Errorf(string, ...any) {}

func testResolver(ask func(ctx context.Context, ep dohEndpoint, q []byte) ([]byte, error)) *proxyResolver {
	p := newProxyResolver(1, nil, quietLog{})
	p.ask = ask
	return p
}

// A silent first endpoint (a dead exit of a rotating proxy) must not hold the query:
// the next endpoint is asked after dohHedge and its answer wins.
func TestDoHRaceHedgesSlowEndpoint(t *testing.T) {
	p := testResolver(func(ctx context.Context, ep dohEndpoint, q []byte) ([]byte, error) {
		if ep.ip == dohEndpoints[0].ip {
			<-ctx.Done()
			return nil, ctx.Err()
		}
		return []byte("answer from " + ep.ip), nil
	})
	start := time.Now()
	got, err := p.race(context.Background(), p.candidates(), []byte("q"))
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != "answer from "+dohEndpoints[1].ip {
		t.Fatalf("answer %q", got)
	}
	if d := time.Since(start); d > dohHedge+time.Second {
		t.Fatalf("took %v, want about %v", d, dohHedge)
	}
	if p.good != dohEndpoints[1].ip {
		t.Fatalf("good endpoint %q", p.good)
	}
	// The cancelled slow try is not a failure of that endpoint.
	if p.fails[dohEndpoints[0].ip] != 0 {
		t.Fatalf("cancelled try counted as a failure")
	}
}

// A failure starts the next endpoint at once, without waiting for the hedge timer.
func TestDoHRaceMovesOnAfterFailure(t *testing.T) {
	p := testResolver(func(ctx context.Context, ep dohEndpoint, q []byte) ([]byte, error) {
		if ep.ip == dohEndpoints[0].ip {
			return nil, errors.New("reset")
		}
		return []byte("ok"), nil
	})
	start := time.Now()
	if _, err := p.race(context.Background(), p.candidates(), []byte("q")); err != nil {
		t.Fatal(err)
	}
	if d := time.Since(start); d > dohHedge/2 {
		t.Fatalf("took %v after an immediate failure", d)
	}
}

// One bad exit does not switch an endpoint off; dohFailLimit failures in a row do,
// and a success in between resets the count.
func TestDoHEndpointRestsOnlyAfterRepeatedFailures(t *testing.T) {
	var fail atomic.Bool
	p := testResolver(func(ctx context.Context, ep dohEndpoint, q []byte) ([]byte, error) {
		if fail.Load() {
			return nil, errors.New("timeout")
		}
		return []byte("ok"), nil
	})
	ep := dohEndpoints[0]
	for i := 0; i < dohFailLimit-1; i++ {
		p.noteFail(ep, errors.New("x"))
	}
	p.noteOK(ep)
	for i := 0; i < dohFailLimit-1; i++ {
		p.noteFail(ep, errors.New("x"))
	}
	if c := p.candidates(); len(c) != len(dohEndpoints) {
		t.Fatalf("endpoint rested after %d failures following a success", dohFailLimit-1)
	}
	p.noteFail(ep, errors.New("x"))
	for _, c := range p.candidates() {
		if c.ip == ep.ip {
			t.Fatalf("endpoint not rested after %d failures in a row", dohFailLimit)
		}
	}
	fail.Store(true)
	if _, err := p.race(context.Background(), p.candidates(), []byte("q")); err == nil {
		t.Fatal("want an error when every endpoint fails")
	}
}

// A caller that gives up stops the race at once.
func TestDoHRaceHonoursCallerDeadline(t *testing.T) {
	p := testResolver(func(ctx context.Context, ep dohEndpoint, q []byte) ([]byte, error) {
		<-ctx.Done()
		return nil, ctx.Err()
	})
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	start := time.Now()
	if _, err := p.race(ctx, p.candidates(), []byte("q")); err == nil {
		t.Fatal("want an error")
	}
	if d := time.Since(start); d > time.Second {
		t.Fatalf("took %v", d)
	}
	for ip, n := range p.fails {
		if n != 0 {
			t.Fatalf("caller cancel counted as failure of %s", ip)
		}
	}
}
