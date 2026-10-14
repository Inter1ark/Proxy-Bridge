// Package rules holds the per-program routing table of pbcore: which proxy a
// program's connections go through, or whether they go direct or are blocked.
//
// A rule set is JSON, written by the GUI to a private file or pushed over the
// control socket:
//
//	{"proxies": [{"id": 1, "url": "socks5://user:pass@host:port"}],
//	 "rules":   [{"process": "/Applications/Telegram.app", "action": "proxy", "proxy": 1},
//	             {"process": "curl", "action": "direct"},
//	             {"process": "*", "action": "proxy", "proxy": 1}]}
//
// Rules are evaluated in order and the first match wins. No match means direct,
// exactly like the Windows core (docs/CORE_API.md).
package rules

import (
	"encoding/json"
	"errors"
	"fmt"
	"path"
	"strings"
)

// Action is what happens to a matched connection.
type Action string

const (
	ActionProxy  Action = "proxy"
	ActionDirect Action = "direct"
	ActionBlock  Action = "block"
)

// Proxy is an upstream proxy referenced by id from rules.
type Proxy struct {
	ID  int    `json:"id"`
	URL string `json:"url"`
}

// Rule routes connections of one program.
type Rule struct {
	// Process is "*" (every program), an absolute path (an .app bundle or an
	// executable) or a bare name ("curl", "Telegram", "Telegram.app").
	Process string `json:"process"`
	Action  Action `json:"action"`
	Proxy   int    `json:"proxy,omitempty"`
}

// Set is a complete routing table.
type Set struct {
	Proxies []Proxy `json:"proxies"`
	Rules   []Rule  `json:"rules"`
}

// Parse reads and validates a JSON rule set.
func Parse(b []byte) (*Set, error) {
	var s Set
	if err := json.Unmarshal(b, &s); err != nil {
		return nil, fmt.Errorf("rules: %w", err)
	}
	if err := s.Validate(); err != nil {
		return nil, err
	}
	return &s, nil
}

// Validate checks ids and actions.
func (s *Set) Validate() error {
	ids := make(map[int]bool, len(s.Proxies))
	for _, p := range s.Proxies {
		if p.ID <= 0 {
			return fmt.Errorf("rules: proxy id %d must be positive", p.ID)
		}
		if ids[p.ID] {
			return fmt.Errorf("rules: duplicate proxy id %d", p.ID)
		}
		if strings.TrimSpace(p.URL) == "" {
			return fmt.Errorf("rules: proxy %d has no url", p.ID)
		}
		ids[p.ID] = true
	}
	for i, r := range s.Rules {
		if strings.TrimSpace(r.Process) == "" {
			return fmt.Errorf("rules: rule %d has no process", i)
		}
		switch r.Action {
		case ActionProxy:
			if !ids[r.Proxy] {
				return fmt.Errorf("rules: rule %d (%s) refers to unknown proxy %d", i, r.Process, r.Proxy)
			}
		case ActionDirect, ActionBlock:
		default:
			return fmt.Errorf("rules: rule %d (%s) has unknown action %q", i, r.Process, r.Action)
		}
	}
	return nil
}

// Empty reports whether the set routes nothing through a proxy.
func (s *Set) Empty() bool {
	return s == nil || len(s.Rules) == 0
}

// Match returns the first rule that applies to a program with this executable
// path, or false when no rule matches (= direct). An empty path only matches "*".
func (s *Set) Match(exePath string) (Rule, bool) {
	if s == nil {
		return Rule{}, false
	}
	for _, r := range s.Rules {
		if MatchProcess(r.Process, exePath) {
			return r, true
		}
	}
	return Rule{}, false
}

var errNoMatch = errors.New("no match")

// MatchProcess reports whether a rule's process pattern applies to exePath.
//
//   - "*" matches everything;
//   - a pattern with "/" is a path: an .app bundle matches every executable
//     inside it (helpers included), any other path must equal the executable or
//     be one of its parent directories;
//   - a bare name matches the executable's file name, the name of the outermost
//     .app bundle the executable lives in, or either of those with the ".app"
//     or ".exe" suffix dropped. Names compare case-insensitively.
func MatchProcess(pattern, exePath string) bool {
	pattern = strings.TrimSpace(pattern)
	if pattern == "*" {
		return true
	}
	if exePath == "" || pattern == "" {
		return false
	}
	if strings.Contains(pattern, "/") {
		p := strings.TrimRight(pattern, "/")
		if exePath == p {
			return true
		}
		return strings.HasPrefix(exePath, p+"/")
	}
	want := strings.ToLower(stripSuffix(pattern))
	if want == "" {
		return false
	}
	if strings.ToLower(stripSuffix(path.Base(exePath))) == want {
		return true
	}
	if bundle := BundleName(exePath); bundle != "" && strings.ToLower(bundle) == want {
		return true
	}
	return false
}

// BundleName returns the outermost ".app" bundle name without the extension
// ("Google Chrome" for ".../Google Chrome.app/Contents/Frameworks/.../Google Chrome Helper"),
// or "" when the executable is not inside a bundle.
func BundleName(exePath string) string {
	lower := strings.ToLower(exePath)
	i := strings.Index(lower, ".app/")
	if i < 0 {
		return ""
	}
	start := strings.LastIndex(exePath[:i], "/") + 1
	return exePath[start:i]
}

// BundlePath returns the outermost ".app" bundle path of an executable, or "".
func BundlePath(exePath string) string {
	lower := strings.ToLower(exePath)
	i := strings.Index(lower, ".app/")
	if i < 0 {
		return ""
	}
	return exePath[:i+4]
}

func stripSuffix(name string) string {
	lower := strings.ToLower(name)
	for _, suf := range []string{".app", ".exe"} {
		if strings.HasSuffix(lower, suf) {
			return name[:len(name)-len(suf)]
		}
	}
	return name
}
