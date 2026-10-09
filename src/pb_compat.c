/*
 * ProxyBridgeCore compatibility layer: implements the exported API contract described in
 * docs/CORE_API.md on top of the ported core internals (multi-proxy store, rule store,
 * relay), plus traffic counters, diagnostics logging and the capture filter builder.
 *
 * Copyright (c) 2026 ProxyBridge Team, MIT License.
 */
#include "pb_internal.h"

// ---------------------------------------------------------------------------------------
// Shared state
// ---------------------------------------------------------------------------------------
volatile LONG64 g_bytes_up = 0;
volatile LONG64 g_bytes_down = 0;
SRWLOCK g_proxy_lock = SRWLOCK_INIT;
UINT32 g_legacy_proxy_id = 0;
HANDLE g_stop_event = NULL;
HANDLE g_relay_ready_event = NULL;

static const char *proto_name(RuleProtocol p)
{
    switch (p)
    {
        case RULE_PROTOCOL_TCP:  return "TCP";
        case RULE_PROTOCOL_UDP:  return "UDP";
        case RULE_PROTOCOL_BOTH: return "TCP+UDP";
        default:                 return "?";
    }
}

const char *pb_action_name(RuleAction action)
{
    switch (action)
    {
        case RULE_ACTION_PROXY:  return "PROXY";
        case RULE_ACTION_DIRECT: return "DIRECT";
        case RULE_ACTION_BLOCK:  return "BLOCK";
        default:                 return "?";
    }
}

static const char *proxy_type_name(ProxyType t)
{
    return t == PROXY_TYPE_HTTP ? "HTTP" : "SOCKS5";
}

// ---------------------------------------------------------------------------------------
// Traffic counters
// ---------------------------------------------------------------------------------------
void pb_reset_counters(void)
{
    InterlockedExchange64(&g_bytes_up, 0);
    InterlockedExchange64(&g_bytes_down, 0);
}

PROXYBRIDGE_API void ProxyBridge_GetTrafficStats(UINT64* bytes_up, UINT64* bytes_down)
{
    if (bytes_up != NULL)
        *bytes_up = (UINT64)InterlockedCompareExchange64(&g_bytes_up, 0, 0);
    if (bytes_down != NULL)
        *bytes_down = (UINT64)InterlockedCompareExchange64(&g_bytes_down, 0, 0);
}

// ---------------------------------------------------------------------------------------
// Rate-limited logging
// ---------------------------------------------------------------------------------------
#define RL_SLOTS        256
#define RL_WINDOW_MS    30000   // the same key is logged at most once per window
#define RL_MAX_PER_SEC  25      // global cap on rate-limited lines per second

typedef struct {
    UINT32 hash;
    ULONGLONG last;
    UINT32 suppressed;
} RL_SLOT;

static RL_SLOT  g_rl_slots[RL_SLOTS];
static SRWLOCK  g_rl_lock = SRWLOCK_INIT;
static ULONGLONG g_rl_second = 0;
static UINT32   g_rl_in_second = 0;
static UINT32   g_rl_dropped = 0;

static UINT32 fnv1a(const char *s)
{
    UINT32 h = 2166136261u;
    while (*s)
    {
        h ^= (unsigned char)*s++;
        h *= 16777619u;
    }
    return h ? h : 1;
}

// Returns TRUE when the line may be logged. *suppressed_out receives how many lines with
// the same key were swallowed since it was last logged, *dropped_out how many lines were
// dropped by the global cap.
static BOOL rl_allow(const char *key, UINT32 *suppressed_out, UINT32 *dropped_out)
{
    UINT32 h = fnv1a(key);
    ULONGLONG now = GetTickCount64();
    BOOL allow = FALSE;

    *suppressed_out = 0;
    *dropped_out = 0;

    AcquireSRWLockExclusive(&g_rl_lock);
    RL_SLOT *slot = &g_rl_slots[h % RL_SLOTS];
    if (slot->hash == h && now - slot->last < RL_WINDOW_MS)
    {
        slot->suppressed++;
    }
    else
    {
        if (now / 1000 != g_rl_second)
        {
            g_rl_second = now / 1000;
            g_rl_in_second = 0;
        }
        if (g_rl_in_second < RL_MAX_PER_SEC)
        {
            g_rl_in_second++;
            *suppressed_out = (slot->hash == h) ? slot->suppressed : 0;
            *dropped_out = g_rl_dropped;
            g_rl_dropped = 0;
            slot->hash = h;
            slot->last = now;
            slot->suppressed = 0;
            allow = TRUE;
        }
        else
        {
            g_rl_dropped++;
        }
    }
    ReleaseSRWLockExclusive(&g_rl_lock);
    return allow;
}

static void rl_emit(const char *line, UINT32 suppressed, UINT32 dropped)
{
    if (dropped > 0)
        log_message("(%u log lines dropped by the rate limiter)", dropped);
    if (suppressed > 0)
        log_message("%s (repeated %u more times)", line, suppressed);
    else
        log_message("%s", line);
}

void pb_log_ratelimited(const char *key, const char *fmt, ...)
{
    if (g_log_callback == NULL)
        return;
    UINT32 suppressed, dropped;
    if (!rl_allow(key, &suppressed, &dropped))
        return;

    char line[LOG_BUFFER_SIZE];
    va_list args;
    va_start(args, fmt);
    vsnprintf(line, sizeof(line), fmt, args);
    va_end(args);
    rl_emit(line, suppressed, dropped);
}

void pb_log_decision(DWORD pid, const char *process_path, BOOL is_ipv6, const void *dest_ip, UINT16 dest_port,
                     BOOL is_udp, UINT32 rule_id, RuleAction action, UINT32 proxy_id, const char *note)
{
    if (g_log_callback == NULL)
        return;

    char dest[64];
    if (is_ipv6)
    {
        char ip6[48];
        inet_ntop(AF_INET6, (void *)dest_ip, ip6, sizeof(ip6));
        snprintf(dest, sizeof(dest), "[%s]:%u", ip6, dest_port);
    }
    else
    {
        char ip4[24];
        format_ip_address(*(const UINT32 *)dest_ip, ip4, sizeof(ip4));
        snprintf(dest, sizeof(dest), "%s:%u", ip4, dest_port);
    }

    const char *proc = process_path ? extract_filename(process_path) : (pid == 0 ? "unknown" : "?");

    char key[160];
    snprintf(key, sizeof(key), "d|%s|%lu|%s|%d|%u", proc, (unsigned long)pid, dest, (int)action, rule_id);
    UINT32 suppressed, dropped;
    if (!rl_allow(key, &suppressed, &dropped))
        return;

    char rule_part[48];
    if (rule_id != 0)
        snprintf(rule_part, sizeof(rule_part), "rule #%u", rule_id);
    else
        snprintf(rule_part, sizeof(rule_part), "no rule");

    char via[300] = "";
    if (action == RULE_ACTION_PROXY)
    {
        PROXY_CONFIG snap;
        if (pb_proxy_snapshot(proxy_id, &snap))
        {
            snprintf(via, sizeof(via), " via proxy #%u (%s %s:%u)", snap.config_id,
                proxy_type_name(snap.type), snap.host, snap.port);
            SecureZeroMemory(snap.password, sizeof(snap.password));
        }
        else
            snprintf(via, sizeof(via), " via proxy #%u", proxy_id);
    }

    char line[LOG_BUFFER_SIZE];
    snprintf(line, sizeof(line), "[CONN] %s (pid %lu) -> %s %s: %s -> %s%s%s%s%s",
        proc, (unsigned long)pid, dest, is_udp ? "UDP" : "TCP", rule_part, pb_action_name(action), via,
        note ? " (" : "", note ? note : "", note ? ")" : "");
    rl_emit(line, suppressed, dropped);
}

// ---------------------------------------------------------------------------------------
// Active relay registry (so Stop can close connections that are still being relayed)
// ---------------------------------------------------------------------------------------
static SRWLOCK g_relay_reg_lock = SRWLOCK_INIT;
static RELAY_PAIR *g_relay_head = NULL;

void pb_relay_register(RELAY_PAIR *pair)
{
    AcquireSRWLockExclusive(&g_relay_reg_lock);
    pair->reg_prev = NULL;
    pair->reg_next = g_relay_head;
    if (g_relay_head != NULL)
        g_relay_head->reg_prev = pair;
    g_relay_head = pair;
    ReleaseSRWLockExclusive(&g_relay_reg_lock);
}

void pb_relay_unregister(RELAY_PAIR *pair)
{
    AcquireSRWLockExclusive(&g_relay_reg_lock);
    if (pair == g_relay_head || pair->reg_prev != NULL)
    {
        if (pair->reg_prev != NULL)
            pair->reg_prev->reg_next = pair->reg_next;
        else
            g_relay_head = pair->reg_next;
        if (pair->reg_next != NULL)
            pair->reg_next->reg_prev = pair->reg_prev;
    }
    pair->reg_next = NULL;
    pair->reg_prev = NULL;
    ReleaseSRWLockExclusive(&g_relay_reg_lock);
}

void pb_relay_shutdown_all(void)
{
    int n = 0;
    // Exclusive: a relay thread unregisters (under this lock) before it closes its sockets
    // and frees the pair, so every pair and socket seen here is still valid.
    AcquireSRWLockExclusive(&g_relay_reg_lock);
    for (RELAY_PAIR *p = g_relay_head; p != NULL; p = p->reg_next)
    {
        shutdown(p->sock_client, SD_BOTH);
        shutdown(p->sock_proxy, SD_BOTH);
        // shutdown() alone does not wake a recv() that is blocked in another thread;
        // cancelling the pending I/O makes it return so the relay thread exits and
        // closes its own sockets (we never close them here, to avoid a double close).
        CancelIoEx((HANDLE)p->sock_client, NULL);
        CancelIoEx((HANDLE)p->sock_proxy, NULL);
        n++;
    }
    ReleaseSRWLockExclusive(&g_relay_reg_lock);
    if (n > 0)
        log_message("Closed %d relayed connection(s)", n);
}

// ---------------------------------------------------------------------------------------
// Capture filter
// ---------------------------------------------------------------------------------------
// Builds the WinDivert filter. Returns the number of proxy addresses excluded.
//  - TCP: everything outbound or on loopback, plus the relay port in either direction.
//  - UDP: only when capture_udp (a BLOCK rule can match UDP); DNS, DHCP and loopback UDP
//    are never captured, so DNS always goes direct.
//  - exclude_proxies: IPv4 packets to the proxy servers are not captured at all.
//    "(ipv6 or ip.DstAddr != X)" is used instead of "ip.DstAddr != X" so IPv6 packets,
//    which have no ip.* fields, are not excluded by accident.
int pb_build_filter(char *buf, size_t size, BOOL capture_udp, BOOL exclude_proxies)
{
    char excl[2048] = "";
    int excluded = 0;

    if (exclude_proxies)
    {
        UINT32 seen[32];
        size_t used = 0;
        AcquireSRWLockShared(&g_proxy_lock);
        for (int i = 0; i < g_proxy_config_count && excluded < 32; i++)
        {
            UINT32 ip = g_proxy_configs[i].resolved_ip;
            if (ip == 0 || (ip & 0xFF) == 127)
                continue;   // loopback proxies are already direct (and needed for loopback relay)
            BOOL dup = FALSE;
            for (int j = 0; j < excluded; j++)
                if (seen[j] == ip) { dup = TRUE; break; }
            if (dup)
                continue;
            char ipstr[24];
            format_ip_address(ip, ipstr, sizeof(ipstr));
            int n = snprintf(excl + used, sizeof(excl) - used, " and (ipv6 or ip.DstAddr != %s)", ipstr);
            if (n <= 0 || (size_t)n >= sizeof(excl) - used)
                break;
            used += (size_t)n;
            seen[excluded++] = ip;
        }
        ReleaseSRWLockShared(&g_proxy_lock);
    }

    char udp[512] = "";
    if (capture_udp)
    {
        snprintf(udp, sizeof(udp),
            " or (udp and outbound and not loopback"
            " and udp.DstPort != 53 and udp.SrcPort != 53"
            " and udp.DstPort != 67 and udp.DstPort != 68"
            " and udp.DstPort != 546 and udp.DstPort != 547)");
    }

    snprintf(buf, size,
        "not impostor and ("
        "(tcp and (outbound or loopback or tcp.DstPort == %u or tcp.SrcPort == %u)%s)"
        "%s)",
        g_local_relay_port, g_local_relay_port, excl, udp);
    return excluded;
}

// ---------------------------------------------------------------------------------------
// Start-up diagnostics
// ---------------------------------------------------------------------------------------
static const char *if_type_name(DWORD t)
{
    switch (t)
    {
        case IF_TYPE_ETHERNET_CSMACD:    return "ethernet";
        case IF_TYPE_IEEE80211:          return "wifi";
        case IF_TYPE_SOFTWARE_LOOPBACK:  return "loopback";
        case IF_TYPE_TUNNEL:             return "tunnel";
        case IF_TYPE_PROP_VIRTUAL:       return "virtual";
        case IF_TYPE_PPP:                return "ppp";
        case IF_TYPE_WWANPP:
        case IF_TYPE_WWANPP2:            return "mobile";
        default:                         return "other";
    }
}

static void wide_to_utf8(const WCHAR *w, char *out, int out_size)
{
    out[0] = '\0';
    if (w != NULL)
        WideCharToMultiByte(CP_UTF8, 0, w, -1, out, out_size, NULL, NULL);
    out[out_size - 1] = '\0';
}

static DWORD best_if_for_ipv4(UINT32 ip)
{
    struct sockaddr_in sa;
    memset(&sa, 0, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = ip;
    DWORD idx = 0;
    if (GetBestInterfaceEx((struct sockaddr *)&sa, &idx) != NO_ERROR)
        return 0;
    return idx;
}

static void log_interfaces(void)
{
    ULONG size = 16 * 1024;
    IP_ADAPTER_ADDRESSES *list = NULL;
    ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    ULONG rc = ERROR_BUFFER_OVERFLOW;

    for (int attempt = 0; attempt < 3 && rc == ERROR_BUFFER_OVERFLOW; attempt++)
    {
        free(list);
        list = (IP_ADAPTER_ADDRESSES *)malloc(size);
        if (list == NULL)
            return;
        rc = GetAdaptersAddresses(AF_UNSPEC, flags, NULL, list, &size);
    }
    if (rc != NO_ERROR)
    {
        log_message("Interfaces: GetAdaptersAddresses failed (%lu)", rc);
        free(list);
        return;
    }

    for (IP_ADAPTER_ADDRESSES *a = list; a != NULL; a = a->Next)
    {
        if (a->OperStatus != IfOperStatusUp)
            continue;
        char name[128], desc[128];
        wide_to_utf8(a->FriendlyName, name, sizeof(name));
        wide_to_utf8(a->Description, desc, sizeof(desc));

        char v4[200] = "";
        size_t used = 0;
        BOOL has_v6 = FALSE;
        for (IP_ADAPTER_UNICAST_ADDRESS *u = a->FirstUnicastAddress; u != NULL; u = u->Next)
        {
            if (u->Address.lpSockaddr->sa_family == AF_INET)
            {
                char ip[24];
                format_ip_address(((struct sockaddr_in *)u->Address.lpSockaddr)->sin_addr.s_addr, ip, sizeof(ip));
                int n = snprintf(v4 + used, sizeof(v4) - used, "%s%s/%u", used ? ", " : "", ip, (unsigned)u->OnLinkPrefixLength);
                if (n > 0 && (size_t)n < sizeof(v4) - used)
                    used += (size_t)n;
            }
            else if (u->Address.lpSockaddr->sa_family == AF_INET6)
                has_v6 = TRUE;
        }
        log_message("Interface #%lu '%s' (%s), %s, mtu %lu, IPv4 %s%s",
            (unsigned long)a->IfIndex, name, desc, if_type_name(a->IfType), (unsigned long)a->Mtu,
            used ? v4 : "none", has_v6 ? ", has IPv6" : "");
    }
    free(list);
}

static void log_default_routes(void)
{
    // Every IPv4 route that acts as a default: 0.0.0.0/0, and the 0.0.0.0/1 + 128.0.0.0/1
    // pair that many VPN clients install to override the physical default route.
    PMIB_IPFORWARD_TABLE2 table = NULL;
    if (GetIpForwardTable2(AF_INET, &table) == NO_ERROR && table != NULL)
    {
        int n = 0;
        for (ULONG i = 0; i < table->NumEntries; i++)
        {
            MIB_IPFORWARD_ROW2 *r = &table->Table[i];
            UINT8 plen = r->DestinationPrefix.PrefixLength;
            UINT32 dst = r->DestinationPrefix.Prefix.Ipv4.sin_addr.s_addr;
            BOOL is_default = (plen == 0) ||
                              (plen == 1 && (dst == 0 || (dst & 0xFF) == 128));
            if (!is_default)
                continue;
            char d[24], nh[24];
            format_ip_address(dst, d, sizeof(d));
            format_ip_address(r->NextHop.Ipv4.sin_addr.s_addr, nh, sizeof(nh));
            log_message("Route %s/%u via %s on interface #%lu (metric %lu)",
                d, (unsigned)plen, r->NextHop.Ipv4.sin_addr.s_addr ? nh : "on-link",
                (unsigned long)r->InterfaceIndex, (unsigned long)r->Metric);
            n++;
        }
        if (n == 0)
            log_message("Route: no IPv4 default route found");
        FreeMibTable(table);
    }

    // The interface Windows actually picks for internet traffic (honours VPN split routes).
    DWORD idx = best_if_for_ipv4(parse_ipv4("1.1.1.1"));
    if (idx != 0)
        log_message("Default route interface (best route to the internet): #%lu", (unsigned long)idx);
    else
        log_message("Default route interface: none (no IPv4 internet route)");
}

static void log_proxies(void)
{
    int count = 0;
    AcquireSRWLockShared(&g_proxy_lock);
    count = g_proxy_config_count;
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        PROXY_CONFIG *c = &g_proxy_configs[i];
        char ip[24];
        format_ip_address(c->resolved_ip, ip, sizeof(ip));
        DWORD idx = best_if_for_ipv4(c->resolved_ip);
        log_message("Proxy #%u: %s %s:%u (resolved %s, auth %s, reached via interface #%lu)%s",
            c->config_id, proxy_type_name(c->type), c->host, c->port, ip,
            c->username[0] ? "yes" : "no", (unsigned long)idx,
            c->config_id == g_legacy_proxy_id ? " [default proxy]" : "");
    }
    ReleaseSRWLockShared(&g_proxy_lock);
    if (count == 0)
        log_message("Proxies: none registered");
}

static void log_rules(void)
{
    int n = 0;
    AcquireSRWLockShared(&g_rules_lock);
    for (PROCESS_RULE *r = rules_list; r != NULL; r = r->next)
    {
        char via[32] = "";
        if (r->action == RULE_ACTION_PROXY)
        {
            if (r->proxy_config_id == 0)
                snprintf(via, sizeof(via), " via default proxy");
            else
                snprintf(via, sizeof(via), " via proxy #%u", r->proxy_config_id);
        }
        log_message("Rule %d: #%u %s process '%s', hosts '%s', ports '%s', %s -> %s%s",
            n + 1, r->rule_id, r->enabled ? "enabled" : "DISABLED", r->process_name,
            r->target_hosts ? r->target_hosts : "*", r->target_ports ? r->target_ports : "*",
            proto_name(r->protocol), pb_action_name(r->action), via);
        n++;
    }
    ReleaseSRWLockShared(&g_rules_lock);
    if (n == 0)
        log_message("Rules: none, all traffic goes direct");
    else
        log_message("Rules are evaluated top to bottom, first match wins, no match goes direct");
}

void pb_log_start_diagnostics(void)
{
    if (g_log_callback == NULL)
        return;
    log_message("Core %s starting: host process pid %lu (never intercepted), TCP relay port %u",
        VERSION, (unsigned long)g_current_process_id, g_local_relay_port);
    log_message("Policy: UDP is never proxied, DNS goes direct, loopback/broadcast/multicast/DHCP and proxy servers go direct");
    log_interfaces();
    log_default_routes();
    log_proxies();
    log_rules();
}

// ---------------------------------------------------------------------------------------
// Proxy API
// ---------------------------------------------------------------------------------------
static void log_proxy_registered(UINT32 id, ProxyType type, const char *host, UINT16 port, const char *user, const char *what)
{
    PROXY_CONFIG snap;
    char ip[24] = "?";
    if (pb_proxy_snapshot(id, &snap))
    {
        format_ip_address(snap.resolved_ip, ip, sizeof(ip));
        SecureZeroMemory(snap.password, sizeof(snap.password));
    }
    log_message("%s #%u: %s %s:%u (resolved %s, auth %s)", what, id, proxy_type_name(type), host, port, ip,
        (user != NULL && user[0] != '\0') ? "yes" : "no");
}

PROXYBRIDGE_API UINT32 ProxyBridge_AddProxy(ProxyType type, const char* proxy_ip, UINT16 proxy_port, const char* username, const char* password)
{
    if (type != PROXY_TYPE_HTTP && type != PROXY_TYPE_SOCKS5)
    {
        log_message("AddProxy rejected: unknown proxy type %d", (int)type);
        return 0;
    }
    if (proxy_ip == NULL || proxy_ip[0] == '\0' || proxy_port == 0)
    {
        log_message("AddProxy rejected: empty host or port 0");
        return 0;
    }

    UINT32 id = pb_proxy_add(type, proxy_ip, proxy_port, username, password, FALSE);
    if (id == 0)
    {
        log_message("AddProxy failed for %s %s:%u (host could not be resolved to IPv4, or table full)",
            proxy_type_name(type), proxy_ip, proxy_port);
        return 0;
    }
    log_proxy_registered(id, type, proxy_ip, proxy_port, username, "Proxy registered");
    return id;
}

PROXYBRIDGE_API void ProxyBridge_ClearProxies(void)
{
    // Removes every proxy registered with AddProxy. The legacy SetProxyConfig proxy (if
    // any) is kept, as in the previous core.
    UINT32 ids[MAX_PROXY_CONFIGS];
    int n = 0;
    AcquireSRWLockShared(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count; i++)
        if (g_proxy_configs[i].config_id != g_legacy_proxy_id)
            ids[n++] = g_proxy_configs[i].config_id;
    ReleaseSRWLockShared(&g_proxy_lock);

    for (int i = 0; i < n; i++)
        pb_proxy_delete(ids[i]);
    log_message("Proxies cleared (%d removed)", n);
}

PROXYBRIDGE_API BOOL ProxyBridge_SetProxyConfig(ProxyType type, const char* proxy_ip, UINT16 proxy_port, const char* username, const char* password)
{
    if (type != PROXY_TYPE_HTTP && type != PROXY_TYPE_SOCKS5)
        return FALSE;
    if (proxy_ip == NULL || proxy_ip[0] == '\0' || proxy_port == 0)
        return FALSE;

    if (g_legacy_proxy_id != 0 &&
        pb_proxy_edit(g_legacy_proxy_id, type, proxy_ip, proxy_port, username, password, FALSE))
    {
        log_proxy_registered(g_legacy_proxy_id, type, proxy_ip, proxy_port, username, "Default proxy updated");
        return TRUE;
    }

    UINT32 id = pb_proxy_add(type, proxy_ip, proxy_port, username, password, FALSE);
    if (id == 0)
    {
        log_message("SetProxyConfig failed for %s %s:%u (host could not be resolved to IPv4)",
            proxy_type_name(type), proxy_ip, proxy_port);
        return FALSE;
    }
    g_legacy_proxy_id = id;
    log_proxy_registered(id, type, proxy_ip, proxy_port, username, "Default proxy registered");
    return TRUE;
}

PROXYBRIDGE_API void ProxyBridge_SetDnsViaProxy(BOOL enable)
{
    // DNS always goes direct in this core; kept so existing callers keep working.
    if (enable)
        log_message("DNS via proxy is not supported by this core: DNS goes direct");
}

PROXYBRIDGE_API void ProxyBridge_SetDisableUdp(BOOL disable)
{
    // UDP is never proxied in this core; kept so existing callers keep working.
    if (!disable)
        log_message("UDP proxying is not supported by this core: UDP goes direct");
}

// ---------------------------------------------------------------------------------------
// Rule API
// ---------------------------------------------------------------------------------------
static BOOL valid_rule_args(RuleProtocol protocol, RuleAction action)
{
    return (protocol == RULE_PROTOCOL_TCP || protocol == RULE_PROTOCOL_UDP || protocol == RULE_PROTOCOL_BOTH) &&
           (action == RULE_ACTION_PROXY || action == RULE_ACTION_DIRECT || action == RULE_ACTION_BLOCK);
}

static const char *or_star(const char *s)
{
    return (s == NULL || s[0] == '\0') ? "*" : s;
}

static UINT32 add_rule_common(const char* process_name, const char* target_hosts, const char* target_ports,
                              RuleProtocol protocol, RuleAction action, UINT32 proxy_id)
{
    if (process_name == NULL || process_name[0] == '\0' || !valid_rule_args(protocol, action))
    {
        log_message("AddRule rejected: empty process name or invalid protocol/action (%d/%d)", (int)protocol, (int)action);
        return 0;
    }
    if (action != RULE_ACTION_PROXY)
        proxy_id = 0;

    UINT32 id = pb_rule_add(process_name, target_hosts, target_ports, NULL, protocol, action, proxy_id);
    if (id == 0)
    {
        log_message("AddRule failed for process '%s' (out of memory)", process_name);
        return 0;
    }

    char via[48] = "";
    char warn[96] = "";
    if (action == RULE_ACTION_PROXY)
    {
        if (proxy_id == 0)
            snprintf(via, sizeof(via), " via default proxy");
        else
            snprintf(via, sizeof(via), " via proxy #%u", proxy_id);
        PROXY_CONFIG snap;
        if (!pb_proxy_snapshot(proxy_id, &snap))
            snprintf(warn, sizeof(warn), " [warning: that proxy is not registered, matches go direct]");
        else
            SecureZeroMemory(snap.password, sizeof(snap.password));
        if (protocol != RULE_PROTOCOL_TCP)
            strncat_s(warn, sizeof(warn), " [UDP part goes direct]", _TRUNCATE);
    }
    log_message("Rule #%u added: process '%s', hosts '%s', ports '%s', %s -> %s%s%s",
        id, process_name, or_star(target_hosts), or_star(target_ports), proto_name(protocol),
        pb_action_name(action), via, warn);
    return id;
}

PROXYBRIDGE_API UINT32 ProxyBridge_AddRule(const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action)
{
    // Legacy rule: PROXY goes through the default proxy (SetProxyConfig, else the first one).
    return add_rule_common(process_name, target_hosts, target_ports, protocol, action, 0);
}

PROXYBRIDGE_API UINT32 ProxyBridge_AddRuleEx(const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action, UINT32 proxy_id)
{
    return add_rule_common(process_name, target_hosts, target_ports, protocol, action, proxy_id);
}

PROXYBRIDGE_API BOOL ProxyBridge_EditRule(UINT32 rule_id, const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action)
{
    if (rule_id == 0 || process_name == NULL || process_name[0] == '\0' || !valid_rule_args(protocol, action))
        return FALSE;
    // The rule keeps the proxy id it was created with.
    return pb_rule_edit(rule_id, process_name, or_star(target_hosts), or_star(target_ports), NULL,
                        protocol, action, PB_PROXY_ID_KEEP);
}

// ---------------------------------------------------------------------------------------
// Proxy connection test (uses the default proxy; does not touch packet interception)
// ---------------------------------------------------------------------------------------
static void tc_append(char *buf, size_t size, const char *fmt, ...)
{
    if (buf == NULL || size == 0)
        return;
    size_t used = strnlen_s(buf, size);
    if (used >= size - 1)
        return;
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf + used, size - used, fmt, args);
    va_end(args);
}

PROXYBRIDGE_API int ProxyBridge_TestConnection(const char* target_host, UINT16 target_port, char* result_buffer, size_t buffer_size)
{
    if (result_buffer != NULL && buffer_size > 0)
        result_buffer[0] = '\0';

    PROXY_CONFIG proxy;
    if (!pb_proxy_snapshot(0, &proxy))
    {
        tc_append(result_buffer, buffer_size, "ERROR: No proxy configured\n");
        return -1;
    }
    if (target_host == NULL || target_host[0] == '\0' || target_port == 0)
    {
        SecureZeroMemory(proxy.password, sizeof(proxy.password));
        tc_append(result_buffer, buffer_size, "ERROR: Invalid target host\n");
        return -1;
    }

    tc_append(result_buffer, buffer_size, "Testing connection to %s:%u via %s proxy %s:%u...\n",
        target_host, target_port, proxy_type_name(proxy.type), proxy.host, proxy.port);

    int ret = -1;
    SOCKET s = INVALID_SOCKET;
    UINT32 target_ip = parse_ipv4(target_host);
    if (target_ip == 0)
    {
        struct addrinfo hints, *res = NULL;
        memset(&hints, 0, sizeof(hints));
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        int gai = getaddrinfo(target_host, NULL, &hints, &res);
        if (gai != 0 || res == NULL)
        {
            tc_append(result_buffer, buffer_size, "ERROR: Failed to resolve hostname %s (%d)\n", target_host, gai);
            goto done;
        }
        target_ip = ((struct sockaddr_in *)res->ai_addr)->sin_addr.s_addr;
        freeaddrinfo(res);
    }
    char tip[24];
    format_ip_address(target_ip, tip, sizeof(tip));
    tc_append(result_buffer, buffer_size, "Resolved %s to %s\n", target_host, tip);

    s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET)
    {
        tc_append(result_buffer, buffer_size, "ERROR: Socket creation failed (%d)\n", WSAGetLastError());
        goto done;
    }
    DWORD timeout = 10000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));

    struct sockaddr_in pa;
    memset(&pa, 0, sizeof(pa));
    pa.sin_family = AF_INET;
    pa.sin_addr.s_addr = proxy.resolved_ip;
    pa.sin_port = htons(proxy.port);

    tc_append(result_buffer, buffer_size, "Connecting to proxy %s:%u...\n", proxy.host, proxy.port);
    if (connect_with_timeout(s, (struct sockaddr *)&pa, sizeof(pa), 10000) == SOCKET_ERROR)
    {
        tc_append(result_buffer, buffer_size, "ERROR: Failed to connect to proxy (%d)\n", WSAGetLastError());
        goto done;
    }
    tc_append(result_buffer, buffer_size, "Connected to proxy server\n");

    if (proxy.type == PROXY_TYPE_SOCKS5)
    {
        if (socks5_connect(s, target_ip, target_port, &proxy) != 0)
        {
            tc_append(result_buffer, buffer_size, "ERROR: SOCKS5 handshake failed (see log for the reply code)\n");
            goto done;
        }
        tc_append(result_buffer, buffer_size, "SOCKS5 handshake successful\n");
    }
    else
    {
        if (http_connect(s, target_ip, target_port, &proxy) != 0)
        {
            tc_append(result_buffer, buffer_size, "ERROR: HTTP CONNECT failed (see log for the status code)\n");
            goto done;
        }
        tc_append(result_buffer, buffer_size, "HTTP CONNECT successful\n");
    }

    char req[512];
    int rn = snprintf(req, sizeof(req),
        "GET / HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nUser-Agent: ProxyBridge/1.0\r\n\r\n", target_host);
    if (rn <= 0 || rn >= (int)sizeof(req) || send_all(s, req, rn) == SOCKET_ERROR)
    {
        tc_append(result_buffer, buffer_size, "ERROR: Failed to send test request (%d)\n", WSAGetLastError());
        goto done;
    }
    tc_append(result_buffer, buffer_size, "Sent HTTP GET request\n");

    char resp[1024];
    int got = recv(s, resp, sizeof(resp) - 1, 0);
    if (got > 0)
    {
        resp[got] = '\0';
        char *status_line = strstr(resp, "HTTP/");
        if (status_line != NULL)
        {
            int status_code = 0;
            const char *sp = strchr(status_line, ' ');
            if (sp != NULL)
                status_code = atoi(sp + 1);
            tc_append(result_buffer, buffer_size, "SUCCESS: Received HTTP %d response (%d bytes)\n", status_code, got);
            ret = 0;
        }
        else
            tc_append(result_buffer, buffer_size, "ERROR: Received data but not valid HTTP response\n");
    }
    else if (got == 0)
        tc_append(result_buffer, buffer_size, "ERROR: Connection closed by remote host (no data received)\n");
    else
    {
        int e = WSAGetLastError();
        if (e == WSAETIMEDOUT)
            tc_append(result_buffer, buffer_size, "ERROR: Connection timeout - no response received\n");
        else
            tc_append(result_buffer, buffer_size, "ERROR: Failed to receive response (%d)\n", e);
    }

done:
    if (s != INVALID_SOCKET)
        closesocket(s);
    SecureZeroMemory(proxy.password, sizeof(proxy.password));
    tc_append(result_buffer, buffer_size, ret == 0 ? "\nProxy connection test PASSED\n" : "\nProxy connection test FAILED\n");
    return ret;
}
