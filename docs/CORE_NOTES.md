# ProxyBridgeCore.dll: implementation notes

The exported API is described in `CORE_API.md`. This file explains how the core works inside, why it works through VPN adapters, what changed compared with the previous core and how to build and test it.

## Source layout

The core is a port of the upstream ProxyBridge Windows core (v4.x, MIT) plus our own compatibility layer.

| File | Role |
| --- | --- |
| `src/ProxyBridge.h` | Public header, the contract with the GUI and the CLI |
| `src/pb_internal.h` | Internal structures, globals and prototypes |
| `src/ProxyBridge.c` | WinDivert packet loop, Start, Stop, DllMain |
| `src/pb_compat.c` | Our exports (AddProxy, AddRuleEx, SetProxyConfig, TestConnection, counters), rate limited logging, start diagnostics, capture filter builder |
| `src/pb_rules.c` | Rule store and matching, per connection decision |
| `src/pb_proxy.c` | Proxy store (up to 64 proxies) |
| `src/pb_relay.c` | Local relay server and per connection relay threads |
| `src/pb_socks5.c`, `src/pb_http.c` | SOCKS5 and HTTP CONNECT handshakes |
| `src/pb_conntrack.c` | Table of redirected connections |
| `src/pb_process.c` | Source port to PID and process name, with a cache |
| `src/pb_dns.c` | DNS answer cache (only used by domain rules, which our API does not expose) |
| `src/pb_util.c` | Logging, parsing and socket helpers |
| `src/tests/core_tests.c` | Unit tests (standalone exe, never starts interception) |

## How redirection works

1. WinDivert captures outbound TCP packets (filter built by `pb_build_filter`). UDP is not captured unless a BLOCK rule could match UDP; DNS and DHCP are never captured; packets to the proxy servers are excluded in the filter.
2. For the first packet of a connection the core finds the owning process from its source port (`GetExtendedTcpTable`), evaluates the rules and caches the decision per source port, so later packets of the same connection cost one bitmap read.
3. DIRECT packets are re-injected unchanged. BLOCK packets are dropped.
4. For PROXY, the connection is recorded (source port to original destination and proxy id). The packet is then reflected: source and destination addresses are swapped, the destination port becomes the relay port 34010, and the packet is injected as inbound on the same interface it was captured on. To the TCP stack it looks like the original server is connecting to the relay.
5. The relay listens on `0.0.0.0:34010` and `[::]:34010`. It accepts the connection, looks up the original destination by the peer port, checks that the peer address is that destination, connects to the chosen proxy (10 s timeout), performs the SOCKS5 or HTTP CONNECT handshake and then copies bytes in both directions with one thread per direction.
6. Packets the relay sends back (source port 34010) are captured outbound, get the original destination port as source port, are swapped back and injected inbound, so the application sees replies from the server it connected to.

The process that loaded the DLL (the GUI) is never intercepted, so the relay's own connections to the proxies and the GUI's update and licence checks go direct.

## Why it works through VPN adapters

The previous core rewrote the destination of redirected packets to `127.0.0.1` but kept them on the interface they were captured on. Windows treats a loopback address arriving on a non loopback interface as invalid and drops it. With a WireGuard (Wintun) or OpenVPN tunnel as the default route the SYN therefore never reached the relay and every proxied connection timed out.

The reflection used now never invents addresses. The packet keeps the interface index it was captured on, and its new destination is the local address the application itself used on that interface (for example a tunnel address such as `10.8.0.2/32` on a WireGuard adapter, or the Wi-Fi address on a plain network). Windows accepts such a packet on any interface type, and the relay receives it because it listens on all addresses. The replies take the same path back on the same interface. Nothing depends on the loopback adapter or on the physical default route.

At Start the log shows every active interface (index, name, type, IPv4), all default routes (including the `0.0.0.0/1` plus `128.0.0.0/1` pair many VPN clients install), the interface Windows actually uses to reach the internet, and for each proxy the interface used to reach it.

## Policy

* Rules are evaluated in insertion order and the first match wins. No match means DIRECT. A `*` (or `ANY`) process rule matches every process; it is not moved to the end (upstream did that).
* `proxy_id` 0 in a PROXY rule means the default proxy: the one set with `SetProxyConfig`, otherwise the first registered proxy. An unknown proxy id makes the rule go DIRECT (upstream silently used the first proxy).
* UDP is never proxied, DNS goes direct. `SetDnsViaProxy` and `SetDisableUdp` are accepted and only log a note.
* Loopback, broadcast, multicast, link local, DHCP and the proxy servers always go direct.
* `ClearProxies` removes proxies added with `AddProxy`; the `SetProxyConfig` proxy is kept, as before.
* `Stop` is idempotent and closes connections that are still being relayed.
* Traffic counters count relayed TCP payload: up is application to proxy, down is proxy to application. Start resets them.
* Passwords are never logged and are wiped from temporary buffers after use.

## Changes compared with the previous core

From upstream (v3.1 to v4.x):

* CVE-2026-24402: the UDP relay bounds checks datagram sizes before copying and copies connection data under the lock instead of using pointers after releasing it. In this build the UDP relay is not started at all.
* Rule list and connection table are protected by reader/writer locks; rule edits from the GUI cannot free memory the packet thread is reading.
* Speed: one ordered packet thread (no TCP reordering), per source port decision cache, 4 MB relay socket buffers, separate upload and download relay threads, larger WinDivert queue.
* CPU and memory: hash tables instead of lists, PID cache with pruning, a cleanup thread for stale entries, leaks fixed.
* IPv6 TCP is redirected instead of dropped; IPv6 rules support exact, CIDR and range hosts.
* Wildcard process matching with several `*` anywhere in the pattern, full path patterns, case insensitive.
* Disabled rules are skipped correctly.

Our own changes on top of the port:

* Compatibility layer for our exports and multi proxy routing (`AddProxy`, `AddRuleEx`, `ClearProxies`, `SetProxyConfig`).
* Strict first match order, unknown proxy ids go direct, proxy servers excluded both in the filter and in the decision.
* TCP only capture filter (UDP only for BLOCK rules), which keeps WireGuard's own UDP traffic out of the packet loop.
* HTTP CONNECT replies are read up to the end of the headers, never further, so replies split over several segments work and tunnel data sent right after the reply is not lost.
* The relay only accepts connections whose peer is the tracked original destination.
* Start waits until the relay listens before diverting packets, and fails cleanly (with a log line) if the relay port is taken. Failed starts release every thread.
* Diagnostic logging: start parameters, interfaces and routes, proxies, rules, rate limited connection decisions, proxy connect failures with the WSA error, SOCKS5 reply codes and HTTP status codes.
* Traffic counters.
* PID cache timestamps are 64 bit (the upstream 32 bit field broke the cache after 49.7 days of uptime).

## Build

Requirements: MinGW-w64 GCC (for example `C:\msys64\mingw64\bin`) and the WinDivert 2.2.2 SDK in `C:\WinDivert-2.2.2-A`.

```
gcc -shared -O2 -Wall -DPROXYBRIDGE_EXPORTS -IC:\WinDivert-2.2.2-A\include ^
    src\ProxyBridge.c src\pb_compat.c src\pb_conntrack.c src\pb_dns.c src\pb_http.c ^
    src\pb_process.c src\pb_proxy.c src\pb_relay.c src\pb_rules.c src\pb_socks5.c src\pb_util.c ^
    -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi -lpsapi -o ProxyBridgeCore.dll
```

`build-installer.ps1` and `build-dll.bat` use the same command. `build-dll.bat tests` builds and runs the unit tests as `output\tests\core_tests.exe`; they need no administrator rights and never start packet interception (only WinDivert's user mode filter helpers are used).

## Testing on a live machine

Interception itself (Start) can only be verified on a machine where cutting the network for a moment is acceptable. Check in the log after Start: the interface list, `Default route interface`, the packet filter line, `Relay listening`, and then `[CONN]` lines with `-> PROXY via proxy #N` for the configured applications. Proxy problems show up as `[RELAY] Failed to connect to proxy` (with the WSA error) or as SOCKS5 reply codes and HTTP status codes.
