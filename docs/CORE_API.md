# ProxyBridgeCore.dll: contract between the native core and the GUI

The GUI (`gui\Interop\ProxyBridgeNative.cs`, `gui\Services\ProxyBridgeService.cs`, `gui\Services\WindowsProxyEngine.cs`) calls these exports. The core may be rewritten internally, but these exports must keep their names, calling convention (cdecl) and semantics.

## Lifecycle
- `void ProxyBridge_SetLogCallback(LogCallback cb)` where `typedef void (*LogCallback)(const char* message)`. The core must log, in plain English, at least: start parameters (relay ports, active interfaces with index and IPv4, default route interface), every proxy registered (type, host:port, resolved IP, auth yes/no; never the password), every rule added, each outbound connection decision (process, destination, rule matched, action, proxy id) rate-limited, every failure to connect to a proxy with the WSA error, SOCKS5/HTTP handshake failures with the reply code, and stop.
- `void ProxyBridge_SetConnectionCallback(ConnectionCallback cb)` (unchanged signature).
- `BOOL ProxyBridge_Start(void)` and `BOOL ProxyBridge_Stop(void)`. Start resets traffic counters. Stop must release the WinDivert handle and all threads even if called twice.
- `void ProxyBridge_GetTrafficStats(UINT64* bytes_up, UINT64* bytes_down)`: cumulative TCP payload bytes relayed through proxies since Start (up = application to proxy).

## Proxies
- `UINT32 ProxyBridge_AddProxy(ProxyType type, const char* host, UINT16 port, const char* user, const char* pass)` returns a proxy id (> 0) or 0 on failure. `ProxyType`: 0 = HTTP (CONNECT), 1 = SOCKS5. Host may be an IP or hostname (resolved once at AddProxy, IPv4 preferred). The proxy's resolved IP must always be excluded from interception.
- `void ProxyBridge_ClearProxies(void)`.
- `BOOL ProxyBridge_SetProxyConfig(...)` (legacy single proxy) keeps working as "proxy id 1 + catch-all rule", for the CLI.

## Rules
- `UINT32 ProxyBridge_AddRuleEx(const char* process, const char* hosts, const char* ports, RuleProtocol proto, RuleAction action, UINT32 proxy_id)` returns a rule id or 0.
  - `process`: exe name (case-insensitive, e.g. `chrome.exe`), a full path, or `*` for any process.
  - `action`: PROXY (use `proxy_id`), DIRECT, BLOCK.
  - Rules are evaluated in insertion order; the first match wins; no match means DIRECT.
  - The GUI builds: one rule per enabled app row (PROXY with its proxy id, or DIRECT), then one catch-all rule `*` for "all other programs" (PROXY with a proxy id, or nothing when they go direct).
- `UINT32 ProxyBridge_AddRule(...)`, `ProxyBridge_DeleteRule`, `EnableRule`, `DisableRule`, `EditRule` keep their current signatures.

## UDP and DNS (3.6.0)

- `ProxyBridge_SetUdpMode(int)`: what happens to UDP of programs whose rule says PROXY. `0` direct (legacy: WebRTC and QUIC reveal the real IP), `1` via the SOCKS5 proxy with UDP ASSOCIATE, blocked when the proxy is HTTP or refuses UDP ASSOCIATE, `2` always blocked. Programs with DIRECT rules or without rules are untouched. `SetDisableUdp(TRUE)` equals mode 2, `FALSE` mode 0. The GUI maps its setting "auto" to 1.
- `ProxyBridge_SetDnsViaProxy(BOOL)`: UDP/53 of every process is answered through the proxy as DNS over TCP (one short-lived thread per query, works with HTTP proxies and with SOCKS5 proxies without UDP). Windows resolves names in the DNS Client service, not in the application, so the proxy comes from `ProxyBridge_SetDnsProxy(id)` (0 = the first PROXY rule's proxy, else the first registered proxy); the GUI passes the catch-all proxy when there is one. Queries addressed to private resolvers (the router) are sent to `ProxyBridge_SetDnsServer("1.1.1.1")` instead; the answer is injected back from the original resolver address so the DNS Client accepts it. DNS over TCP from applications themselves is a normal TCP flow and follows the rules.
- The capture filter includes UDP only when needed (mode 1 or 2, a UDP BLOCK rule, or DNS via proxy); DHCP is never captured.
- Rules are registered with protocol BOTH by the GUI; the UDP mode decides, not the rule.
- The proxy checker probes SOCKS5 UDP ASSOCIATE with a STUN Binding Request (`gui/Services/Socks5UdpProbe.cs`) and stores `UdpSupported` and the UDP exit IP with the check result; the list shows a "UDP" or "без UDP" chip.

## Behaviour requirements
- Works when the default route goes through a VPN tunnel adapter (WireGuard/Wintun, OpenVPN TAP) as well as on plain Ethernet/Wi-Fi: redirected packets must reach the local relay without relying on loopback rewriting that Windows drops on non-loopback interfaces.
- The process that loaded the DLL (the GUI) is never intercepted, so its own checks and license calls go direct.
- Loopback, LAN broadcast/multicast, DHCP and the proxy servers themselves always go direct.
- UDP is not proxied (goes direct); DNS goes direct.
- No known memory-safety issues (the upstream CVE-2026-24402 fixes must be present).

## macOS core (pbcore)

On macOS the GUI (`gui\Services\MacProxyEngine.cs`) drives the bundled `pbcore` helper (source in `core-go/`) instead of a DLL. The semantics above hold: proxies have ids, rules are evaluated in insertion order, the first match wins, no match means DIRECT, the GUI's own process is never intercepted. The differences:

- pbcore runs as root (one administrator prompt per connect) and gets the whole table at start as JSON in `--rules-file` (the file is deleted after reading):
  `{"proxies":[{"id":1,"url":"socks5://user:pass@host:port"}],"rules":[{"process":"/Applications/Telegram.app","action":"proxy","proxy":1},{"process":"curl","action":"direct"},{"process":"*","action":"proxy","proxy":1}]}`
  `action` is `proxy`, `direct` or `block`.
- `process` is an `.app` bundle path (matches every executable inside it, helpers included), an executable path, or a bare name that matches the executable's file name or the bundle name (case-insensitive, `.app`/`.exe` suffix optional). The GUI sends the bundle or executable path when it knows it, else the name.
- The owner of a connection is found through the `net.inet.tcp.pcblist_n` sysctl (pid of the socket) and `kern.procargs2` (executable path); when that self-test fails at start, `netstat -anv` is parsed instead. The method is reported in the `up` status line (`"procmap":"pcblist"` or `"netstat"`).
- Rules can be replaced while running: control socket command `rules <token> <base64 json>` answers `ok` or `error <reason>`; open connections keep their decision, new ones follow the new table. `MacProxyEngine` implements `ILiveRules.CommitRulesAsync()` for this, so the GUI never restarts the helper (and never asks for the password again) when the table changes.
- Direct connections and connections to proxies leave through the interface of the original default route (`IP_BOUND_IF`), so they never re-enter the tunnel. UDP is not proxied; DNS over UDP goes direct to `--dns`.

### Daemon mode (no password per connect)

Since 3.5.1 the GUI installs pbcore once as a launchd daemon (`mac/install-helper.sh`, one administrator prompt): a root-owned copy at `/Library/PrivilegedHelperTools/org.proxybridge.pbcore`, the job `/Library/LaunchDaemons/org.proxybridge.pbcore.plist` running `pbcore --daemon --control-port 34050 --token-file "/Library/Application Support/ProxyBridge/control.token"`, and the token file readable by root and the `admin` group only (that is what restricts the control socket to administrators). The daemon keeps no tunnel until asked:

- `ping <token>` answers `ok <version>`; the GUI reinstalls the daemon (one prompt) when the version differs from the bundled pbcore.
- `start <token> <base64 json>` brings the tunnel up and answers `ok` after the `up` status line, or `error <reason>`. The JSON is the rule set plus `"dns"` (direct resolver, `""` drops DNS over UDP), `"parent_pid"` (the tunnel goes down when that process disappears) and `"exempt_pids"`. A running tunnel is replaced.
- `stop <token>` tears the tunnel down (status `down`), the daemon stays. `rules` and `watch` work as before; a new watcher first receives the last status line (`idle`, `up`, `down` or `error`).
- `mac/uninstall-helper.sh` (Settings, "Remove helper") removes the job, the binary and the token.

Without the daemon (install refused or failed) the GUI falls back to launching pbcore for one session with `--rules-file`, as in 3.5.0. The Codemagic workflow exercises both paths against a real proxy.

### UDP and DNS on macOS (3.6.0)

pbcore takes the same options as the Windows core in the rule JSON / start request: `"udp": "direct" | "proxy" | "block"`, `"dns_via_proxy": true`, `"dns_proxy": <id>`. UDP flows are attributed through `net.inet.udp.pcblist_n` (or `netstat -anv -p udp`). `proxy` opens one SOCKS5 UDP association per client socket (`internal/proxy/udp.go`, `router_udp.go`) and demultiplexes replies by source; a proxy that answers UDP ASSOCIATE with "command not supported" has its UDP blocked for 60 s, so nothing leaks. `block` and a rule's BLOCK answer with ICMP port unreachable, so applications fall back to TCP at once. DNS via proxy sends udp/53 (from mDNSResponder, like the Windows DNS Client) as DNS over TCP through the DNS proxy; private resolvers are replaced by the `--dns` server. The Codemagic step "UDP end-to-end test" needs `TEST_PROXY_UDP` (a SOCKS5 proxy with UDP) next to `TEST_PROXY` (one without).
