package rules

import "testing"

func TestMatchProcess(t *testing.T) {
	chromeHelper := "/Applications/Google Chrome.app/Contents/Frameworks/Google Chrome Framework.framework/Versions/140/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)"
	cases := []struct {
		pattern, exe string
		want         bool
	}{
		{"*", "/usr/bin/curl", true},
		{"*", "", true},
		{"curl", "", false},
		{"curl", "/usr/bin/curl", true},
		{"CURL", "/usr/bin/curl", true},
		{"curl.exe", "/usr/bin/curl", true},
		{"curl", "/usr/bin/curly", false},
		{"/usr/bin/curl", "/usr/bin/curl", true},
		{"/usr/bin/curl", "/usr/bin/curl2", false},
		{"/usr/bin", "/usr/bin/curl", true},
		{"/Applications/Telegram.app", "/Applications/Telegram.app/Contents/MacOS/Telegram", true},
		{"/Applications/Telegram.app/", "/Applications/Telegram.app/Contents/MacOS/Telegram", true},
		{"/Applications/Telegram.app", "/Applications/Telegram Lite.app/Contents/MacOS/Telegram", false},
		{"Telegram", "/Applications/Telegram.app/Contents/MacOS/Telegram", true},
		{"telegram.app", "/Applications/Telegram.app/Contents/MacOS/Telegram", true},
		{"Google Chrome", chromeHelper, true},
		{"Google Chrome.app", chromeHelper, true},
		{"/Applications/Google Chrome.app", chromeHelper, true},
		{"Google Chrome Helper (Renderer)", chromeHelper, true},
		{"Chrome", chromeHelper, false},
		{"python3", "/opt/homebrew/Cellar/python@3.12/3.12.4/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python", false},
		{"Python", "/opt/homebrew/Cellar/python@3.12/3.12.4/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python", true},
	}
	for _, c := range cases {
		if got := MatchProcess(c.pattern, c.exe); got != c.want {
			t.Errorf("MatchProcess(%q, %q) = %v, want %v", c.pattern, c.exe, got, c.want)
		}
	}
}

func TestBundleName(t *testing.T) {
	if got := BundleName("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"); got != "Google Chrome" {
		t.Fatalf("got %q", got)
	}
	if got := BundlePath("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"); got != "/Applications/Google Chrome.app" {
		t.Fatalf("got %q", got)
	}
	if got := BundleName("/usr/bin/curl"); got != "" {
		t.Fatalf("got %q", got)
	}
}

func TestParseAndMatch(t *testing.T) {
	s, err := Parse([]byte(`{"proxies":[{"id":1,"url":"socks5://u:p@h:1080"},{"id":2,"url":"http://h:8080"}],
		"rules":[{"process":"/Applications/Telegram.app","action":"proxy","proxy":2},
		         {"process":"curl","action":"direct"},
		         {"process":"nc","action":"block"},
		         {"process":"*","action":"proxy","proxy":1}]}`))
	if err != nil {
		t.Fatal(err)
	}
	r, ok := s.Match("/Applications/Telegram.app/Contents/MacOS/Telegram")
	if !ok || r.Action != ActionProxy || r.Proxy != 2 {
		t.Fatalf("telegram: %+v %v", r, ok)
	}
	r, ok = s.Match("/usr/bin/curl")
	if !ok || r.Action != ActionDirect {
		t.Fatalf("curl: %+v %v", r, ok)
	}
	r, ok = s.Match("/usr/bin/nc")
	if !ok || r.Action != ActionBlock {
		t.Fatalf("nc: %+v %v", r, ok)
	}
	r, ok = s.Match("/Applications/Safari.app/Contents/MacOS/Safari")
	if !ok || r.Proxy != 1 {
		t.Fatalf("safari: %+v %v", r, ok)
	}
	// unknown program: the catch-all still applies
	if r, ok = s.Match(""); !ok || r.Proxy != 1 {
		t.Fatalf("unknown: %+v %v", r, ok)
	}

	noCatchAll, _ := Parse([]byte(`{"proxies":[{"id":1,"url":"socks5://h:1080"}],"rules":[{"process":"curl","action":"proxy","proxy":1}]}`))
	if _, ok := noCatchAll.Match("/usr/bin/python3"); ok {
		t.Fatal("python3 must not match")
	}
}

func TestValidate(t *testing.T) {
	bad := []string{
		`{"proxies":[{"id":0,"url":"x"}],"rules":[]}`,
		`{"proxies":[{"id":1,"url":"x"},{"id":1,"url":"y"}],"rules":[]}`,
		`{"proxies":[],"rules":[{"process":"*","action":"proxy","proxy":1}]}`,
		`{"proxies":[],"rules":[{"process":"*","action":"teleport"}]}`,
		`{"proxies":[],"rules":[{"process":"","action":"direct"}]}`,
		`not json`,
	}
	for _, b := range bad {
		if _, err := Parse([]byte(b)); err == nil {
			t.Errorf("expected error for %s", b)
		}
	}
	if _, err := Parse([]byte(`{"proxies":[],"rules":[]}`)); err != nil {
		t.Fatal(err)
	}
}
