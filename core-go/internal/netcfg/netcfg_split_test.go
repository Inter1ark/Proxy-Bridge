package netcfg

import (
	"strings"
	"testing"
)

func TestSplitPrefixes(t *testing.T) {
	cases := map[int]string{
		1: "0.0.0.0/1 128.0.0.0/1",
		2: "0.0.0.0/2 64.0.0.0/2 128.0.0.0/2 192.0.0.0/2",
		3: "0.0.0.0/3 32.0.0.0/3 64.0.0.0/3 96.0.0.0/3 128.0.0.0/3 160.0.0.0/3 192.0.0.0/3 224.0.0.0/3",
	}
	for bits, want := range cases {
		var got []string
		for _, p := range splitPrefixes(bits) {
			got = append(got, p.String())
		}
		if strings.Join(got, " ") != want {
			t.Errorf("/%d: got %q, want %q", bits, strings.Join(got, " "), want)
		}
	}
	defer func() {
		if recover() == nil {
			t.Error("length 0 must panic")
		}
	}()
	splitPrefixes(0)
}
