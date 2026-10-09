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
