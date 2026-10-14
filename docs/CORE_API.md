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
- The owner of a connection is found through the `net.inet.tcp.pcblist_n` sysctl (pid of the socket) and `kern.procargs2` (executable path); when that self-test fails at start, `lsof` is used. The method is reported in the `up` status line (`"procmap":"pcblist"` or `"lsof"`).
- Rules can be replaced while running: control socket command `rules <token> <base64 json>` answers `ok` or `error <reason>`; open connections keep their decision, new ones follow the new table. `MacProxyEngine` implements `ILiveRules.CommitRulesAsync()` for this, so the GUI never restarts the helper (and never asks for the password again) when the table changes.
- Direct connections and connections to proxies leave through the interface of the original default route (`IP_BOUND_IF`), so they never re-enter the tunnel. UDP is not proxied; DNS over UDP goes direct to `--dns`.
