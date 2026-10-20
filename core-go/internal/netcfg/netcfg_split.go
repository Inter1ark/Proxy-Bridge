package netcfg

import (
	"fmt"
	"net/netip"
)

// splitPrefixes lists the IPv4 prefixes of the given length that together cover
// the whole address space: /1 gives 0.0.0.0/1 and 128.0.0.0/1, /2 four blocks and
// so on. Longer prefixes win the route lookup, which lets the tunnel take over
// from a VPN that already holds the /1 pair.
func splitPrefixes(bits int) []netip.Prefix {
	if bits < 1 || bits > 8 {
		panic(fmt.Sprintf("splitPrefixes: unsupported length %d", bits))
	}
	n := 1 << bits
	out := make([]netip.Prefix, 0, n)
	for i := 0; i < n; i++ {
		first := byte(i << (8 - bits))
		out = append(out, netip.PrefixFrom(netip.AddrFrom4([4]byte{first, 0, 0, 0}), bits))
	}
	return out
}

// maxSplitBits is the longest split tried before giving up: /1 (nothing else
// in the way), /2 (one VPN holding /1), /3 (a VPN that itself fell back to /2).
const maxSplitBits = 3
