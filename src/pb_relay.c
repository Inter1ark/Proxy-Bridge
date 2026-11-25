/*
 * ProxyBridgeCore relay: local TCP relay server and per-connection worker threads
 * (the SOCKS5 UDP relay is kept but not started, UDP is never proxied by this build).
 *
 * Ported from the ProxyBridge Windows core (v4.x),
 * Copyright (c) 2025 Anof-cyber/InterceptSuite, MIT License.
 * Modifications copyright (c) 2026 ProxyBridge Team, MIT License:
 *   proxy settings copied per connection (safe against concurrent edits), bounded proxy
 *   connect with WSA error logging, handshake failure logging, traffic counters,
 *   active-relay registry so Stop can close relayed connections, the relay only accepts
 *   connections whose peer is the tracked original destination.
 */
#include "pb_internal.h"

// ---------------------------------------------------------------------------------------
// DNS over TCP through the proxy. One short-lived thread per query: the relay loop must
// never block on a proxy round trip, and DNS volume is small.
// ---------------------------------------------------------------------------------------
typedef struct {
    PROXY_CONFIG cfg;
    UINT32 resolver_ip;            // network order; where the app sent the query
    struct sockaddr_in client;     // where the answer goes back (through the relay socket)
    int query_len;
    unsigned char query[1024];
} DNS_JOB;

static volatile LONG g_dns_jobs = 0;
#define DNS_MAX_JOBS 64

BOOL pb_dns_tcp_exchange(const PROXY_CONFIG *cfg, UINT32 resolver_ip, const unsigned char *query, int query_len,
                         unsigned char *answer, int answer_cap, int *answer_len)
{
    return pb_dns_tcp_exchange_port(cfg, resolver_ip, 53, query, query_len, answer, answer_cap, answer_len);
}

// Open DNS over TCP connections through a proxy are kept for reuse (RFC 7766: several
// queries, one after another, on one connection). With a proxy that rotates the exit on
// every connection, a connection per query meant a new exit per name and many of them
// never answered; a kept connection stays on an exit that works. Each connection carries
// one query at a time; a connection that failed is closed, never returned.
#define DNS_POOL_SLOTS 32
#define DNS_POOL_IDLE_MS 10000
typedef struct { UINT32 cfg_id; UINT32 ip; UINT16 port; SOCKET s; ULONGLONG last; } DNS_POOL_ENTRY;
static DNS_POOL_ENTRY g_dns_pool[DNS_POOL_SLOTS];
static SRWLOCK g_dns_pool_lock = SRWLOCK_INIT;
static BOOL g_dns_pool_ready = FALSE;

static void dns_pool_init_locked(void)
{
    if (g_dns_pool_ready) return;
    for (int i = 0; i < DNS_POOL_SLOTS; i++) g_dns_pool[i].s = INVALID_SOCKET;
    g_dns_pool_ready = TRUE;
}

static SOCKET dns_pool_take(UINT32 cfg_id, UINT32 ip, UINT16 port)
{
    SOCKET s = INVALID_SOCKET;
    ULONGLONG now = GetTickCount64();
    AcquireSRWLockExclusive(&g_dns_pool_lock);
    dns_pool_init_locked();
    for (int i = 0; i < DNS_POOL_SLOTS; i++)
    {
        DNS_POOL_ENTRY *e = &g_dns_pool[i];
        if (e->s == INVALID_SOCKET) continue;
        if (now - e->last > DNS_POOL_IDLE_MS)
        {
            closesocket(e->s);   // resolvers close idle TCP sessions: do not bet a query on it
            e->s = INVALID_SOCKET;
            continue;
        }
        if (s == INVALID_SOCKET && e->cfg_id == cfg_id && e->ip == ip && e->port == port)
        {
            s = e->s;
            e->s = INVALID_SOCKET;
        }
    }
    ReleaseSRWLockExclusive(&g_dns_pool_lock);
    return s;
}

static void dns_pool_put(UINT32 cfg_id, UINT32 ip, UINT16 port, SOCKET s)
{
    AcquireSRWLockExclusive(&g_dns_pool_lock);
    dns_pool_init_locked();
    int slot = -1;
    for (int i = 0; i < DNS_POOL_SLOTS && slot < 0; i++)
        if (g_dns_pool[i].s == INVALID_SOCKET) slot = i;
    if (slot >= 0)
    {
        g_dns_pool[slot].cfg_id = cfg_id;
        g_dns_pool[slot].ip = ip;
        g_dns_pool[slot].port = port;
        g_dns_pool[slot].s = s;
        g_dns_pool[slot].last = GetTickCount64();
        s = INVALID_SOCKET;
    }
    ReleaseSRWLockExclusive(&g_dns_pool_lock);
    if (s != INVALID_SOCKET) closesocket(s);   // pool full
}

void pb_dns_pool_clear(void)
{
    AcquireSRWLockExclusive(&g_dns_pool_lock);
    dns_pool_init_locked();
    for (int i = 0; i < DNS_POOL_SLOTS; i++)
        if (g_dns_pool[i].s != INVALID_SOCKET) { closesocket(g_dns_pool[i].s); g_dns_pool[i].s = INVALID_SOCKET; }
    ReleaseSRWLockExclusive(&g_dns_pool_lock);
}

// One length-prefixed query and its answer on an open tunnel to the resolver.
static BOOL dns_tcp_roundtrip(SOCKET s, const unsigned char *query, int query_len,
                              unsigned char *answer, int answer_cap, int *answer_len)
{
    unsigned char len2[2] = { (unsigned char)(query_len >> 8), (unsigned char)query_len };
    if (send_all(s, (const char *)len2, 2) == SOCKET_ERROR || send_all(s, (const char *)query, query_len) == SOCKET_ERROR)
        return FALSE;
    if (recv_n(s, (char *)len2, 2) != 2)
        return FALSE;
    int n = (len2[0] << 8) | len2[1];
    if (n < 12 || n > answer_cap || recv_n(s, (char *)answer, n) != n)
        return FALSE;
    if (answer[0] != query[0] || answer[1] != query[1])
        return FALSE;   // not the answer to this query: the stream is out of step
    *answer_len = n;
    return TRUE;
}

BOOL pb_dns_tcp_exchange_port(const PROXY_CONFIG *cfg, UINT32 resolver_ip, UINT16 port, const unsigned char *query, int query_len,
                              unsigned char *answer, int answer_cap, int *answer_len)
{
    *answer_len = 0;
    if (query_len < 12 || query_len > 65535)
        return FALSE;

    SOCKET s = dns_pool_take(cfg->config_id, resolver_ip, port);
    if (s != INVALID_SOCKET)
    {
        if (dns_tcp_roundtrip(s, query, query_len, answer, answer_cap, answer_len))
        {
            dns_pool_put(cfg->config_id, resolver_ip, port, s);
            return TRUE;
        }
        closesocket(s);   // closed or stuck: a fresh tunnel below
    }

    UINT32 proxy_ip = cfg->resolved_ip ? cfg->resolved_ip : resolve_hostname(cfg->host);
    if (proxy_ip == 0)
        return FALSE;
    s = socket(AF_INET, SOCK_STREAM, 0);
    if (s == INVALID_SOCKET)
        return FALSE;
    configure_tcp_socket(s, 65536, 5000);
    struct sockaddr_in pa;
    memset(&pa, 0, sizeof(pa));
    pa.sin_family = AF_INET;
    pa.sin_addr.s_addr = proxy_ip;
    pa.sin_port = htons(cfg->port);
    BOOL ok = FALSE;
    if (connect_with_timeout(s, (struct sockaddr *)&pa, sizeof(pa), 4000) != SOCKET_ERROR)
    {
        int rc = cfg->type == PROXY_TYPE_SOCKS5 ? socks5_connect(s, resolver_ip, port, cfg)
                                                : http_connect(s, resolver_ip, port, cfg);
        if (rc == 0)
            ok = dns_tcp_roundtrip(s, query, query_len, answer, answer_cap, answer_len);
    }
    if (ok)
        dns_pool_put(cfg->config_id, resolver_ip, port, s);
    else
        closesocket(s);
    return ok;
}

// Many residential and mobile proxies refuse port 53 (SOCKS5 reply 2) or accept the CONNECT
// and drop every byte. After such a failure the proxy is marked for DNS_BROKEN_MS and its
// queries go to OpenDNS, which answers plain DNS over TCP on port 443, through the same
// proxy. Queries are never sent around the proxy: the resolver must not see the real address.
#define DNS_BROKEN_SLOTS 64
#define DNS_BROKEN_MS    (10 * 60 * 1000)
static struct { UINT32 id; ULONGLONG until; } g_dns_broken[DNS_BROKEN_SLOTS];
static SRWLOCK g_dns_broken_lock = SRWLOCK_INIT;

BOOL pb_dns_proxy_broken(UINT32 config_id, ULONGLONG now_ms)
{
    BOOL broken = FALSE;
    AcquireSRWLockShared(&g_dns_broken_lock);
    for (int i = 0; i < DNS_BROKEN_SLOTS; i++)
        if (g_dns_broken[i].id == config_id && g_dns_broken[i].until > now_ms) { broken = TRUE; break; }
    ReleaseSRWLockShared(&g_dns_broken_lock);
    return broken;
}

void pb_dns_proxy_mark_broken(UINT32 config_id, ULONGLONG now_ms)
{
    AcquireSRWLockExclusive(&g_dns_broken_lock);
    int slot = -1;
    for (int i = 0; i < DNS_BROKEN_SLOTS; i++)
    {
        if (g_dns_broken[i].id == config_id) { slot = i; break; }
        if (slot < 0 && g_dns_broken[i].until <= now_ms) slot = i;
    }
    if (slot < 0) slot = 0;
    g_dns_broken[slot].id = config_id;
    g_dns_broken[slot].until = now_ms + DNS_BROKEN_MS;
    ReleaseSRWLockExclusive(&g_dns_broken_lock);
}

// ---- regular DNS while a proxy is unreachable -------------------------------------------
// With DNS via proxy every lookup of the computer goes through a proxy: they come from the
// DNS Client service, so they cannot be told apart by program. When that proxy cannot be
// reached nothing resolves, and programs without a rule lose the internet together with the
// proxied ones. While "all other programs" go direct, a proxy that failed is left alone for
// DNS_DEAD_MS and lookups are answered by the resolver the system asked in the first place.
// A proxied program still connects only through its proxy: what leaves around the proxy
// during an outage is the name it asked for, never its traffic.
#define DNS_DEAD_MS (45 * 1000)
static struct { UINT32 id; ULONGLONG until; } g_dns_dead[DNS_BROKEN_SLOTS];
static SRWLOCK g_dns_dead_lock = SRWLOCK_INIT;

BOOL pb_dns_proxy_dead(UINT32 config_id, ULONGLONG now_ms)
{
    BOOL dead = FALSE;
    AcquireSRWLockShared(&g_dns_dead_lock);
    for (int i = 0; i < DNS_BROKEN_SLOTS; i++)
        if (g_dns_dead[i].id == config_id && g_dns_dead[i].until > now_ms) { dead = TRUE; break; }
    ReleaseSRWLockShared(&g_dns_dead_lock);
    return dead;
}

void pb_dns_proxy_mark_dead(UINT32 config_id, ULONGLONG now_ms)
{
    AcquireSRWLockExclusive(&g_dns_dead_lock);
    int slot = -1;
    for (int i = 0; i < DNS_BROKEN_SLOTS; i++)
    {
        if (g_dns_dead[i].id == config_id) { slot = i; break; }
        if (slot < 0 && g_dns_dead[i].until <= now_ms) slot = i;
    }
    if (slot < 0) slot = 0;
    g_dns_dead[slot].id = config_id;
    g_dns_dead[slot].until = now_ms + DNS_DEAD_MS;
    ReleaseSRWLockExclusive(&g_dns_dead_lock);
}

// Source ports of the core's own lookups to the regular DNS, host byte order. The packet
// path lets them through untouched (pb_dns_direct_port), so a lookup can never be captured
// and sent to the DNS proxy again.
#define DNS_DIRECT_PORTS 32
static volatile LONG g_dns_direct_ports[DNS_DIRECT_PORTS];

BOOL pb_dns_direct_port(UINT16 port_host_order)
{
    if (port_host_order == 0)
        return FALSE;
    for (int i = 0; i < DNS_DIRECT_PORTS; i++)
        if ((UINT16)g_dns_direct_ports[i] == port_host_order)
            return TRUE;
    return FALSE;
}

// One lookup over UDP straight to resolver_ip:53. The source port is registered before the
// first packet leaves.
static BOOL dns_udp_direct(UINT32 resolver_ip, const unsigned char *query, int query_len,
                           unsigned char *answer, int answer_cap, int *answer_len)
{
    if (query_len < 12)
        return FALSE;
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET)
        return FALSE;
    BOOL ok = FALSE;
    int slot = -1;
    struct sockaddr_in local;
    memset(&local, 0, sizeof(local));
    local.sin_family = AF_INET;
    int len = sizeof(local);
    if (bind(s, (struct sockaddr *)&local, sizeof(local)) == 0 &&
        getsockname(s, (struct sockaddr *)&local, &len) == 0)
    {
        LONG port = ntohs(local.sin_port);
        for (int i = 0; i < DNS_DIRECT_PORTS && slot < 0; i++)
            if (InterlockedCompareExchange(&g_dns_direct_ports[i], port, 0) == 0)
                slot = i;
    }
    if (slot >= 0)
    {
        DWORD timeout_ms = 3000;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout_ms, sizeof(timeout_ms));
        struct sockaddr_in to;
        memset(&to, 0, sizeof(to));
        to.sin_family = AF_INET;
        to.sin_addr.s_addr = resolver_ip;
        to.sin_port = htons(53);
        if (sendto(s, (const char *)query, query_len, 0, (struct sockaddr *)&to, sizeof(to)) == query_len)
        {
            for (int tries = 0; tries < 4 && !ok; tries++)
            {
                int n = recv(s, (char *)answer, answer_cap, 0);
                if (n <= 0)
                    break;
                if (n >= 12 && answer[0] == query[0] && answer[1] == query[1])
                {
                    *answer_len = n;
                    ok = TRUE;
                }
            }
        }
    }
    closesocket(s);
    if (slot >= 0)
        InterlockedExchange(&g_dns_direct_ports[slot], 0);
    return ok;
}

void pb_dns_proxy_clear_broken(void)
{
    AcquireSRWLockExclusive(&g_dns_broken_lock);
    memset(g_dns_broken, 0, sizeof(g_dns_broken));
    ReleaseSRWLockExclusive(&g_dns_broken_lock);
    AcquireSRWLockExclusive(&g_dns_dead_lock);
    memset(g_dns_dead, 0, sizeof(g_dns_dead));
    ReleaseSRWLockExclusive(&g_dns_dead_lock);
}

// ---- proxies without IPv6 ------------------------------------------------------------
// Per-thread flag through the Win32 TLS API: compiler TLS (__thread) makes MinGW import
// libgcc_s_seh-1.dll (emutls), which is not shipped, and then the DLL does not load at all.
static DWORD g_refused_tls = TLS_OUT_OF_INDEXES;
static INIT_ONCE g_refused_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK refused_tls_init(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    (void)once; (void)param; (void)ctx;
    g_refused_tls = TlsAlloc();
    return TRUE;
}
void pb_set_connect_refused(BOOL refused)
{
    InitOnceExecuteOnce(&g_refused_once, refused_tls_init, NULL, NULL);
    if (g_refused_tls != TLS_OUT_OF_INDEXES)
        TlsSetValue(g_refused_tls, refused ? (LPVOID)1 : NULL);
}
BOOL pb_last_connect_refused(void)
{
    InitOnceExecuteOnce(&g_refused_once, refused_tls_init, NULL, NULL);
    return g_refused_tls != TLS_OUT_OF_INDEXES && TlsGetValue(g_refused_tls) != NULL;
}

#define NO_V6_SLOTS 64
static struct { UINT32 id; int refusals; BOOL no_v6; } g_v6[NO_V6_SLOTS];
static SRWLOCK g_v6_lock = SRWLOCK_INIT;
static volatile LONG g_v6_gen = 0;   // bumped on clear: a probe from an older session is ignored

BOOL pb_proxy_no_ipv6(UINT32 config_id)
{
    BOOL no = FALSE;
    AcquireSRWLockShared(&g_v6_lock);
    for (int i = 0; i < NO_V6_SLOTS; i++)
        if (g_v6[i].id == config_id && config_id != 0) { no = g_v6[i].no_v6; break; }
    ReleaseSRWLockShared(&g_v6_lock);
    return no;
}

static BOOL note_ipv6_locked(UINT32 config_id, BOOL refused, int weight)
{
    int slot = -1;
    for (int i = 0; i < NO_V6_SLOTS; i++)
    {
        if (g_v6[i].id == config_id) { slot = i; break; }
        if (slot < 0 && g_v6[i].id == 0) slot = i;
    }
    if (slot < 0)
    {
        if (!refused) return FALSE;
        slot = 0;
        memset(&g_v6[0], 0, sizeof(g_v6[0]));
    }
    if (g_v6[slot].id != config_id)
    {
        if (!refused) return FALSE;
        g_v6[slot].id = config_id;
        g_v6[slot].refusals = 0;
        g_v6[slot].no_v6 = FALSE;
    }
    if (!refused)
    {
        g_v6[slot].refusals = 0;   // an IPv6 tunnel worked: the proxy has IPv6
        return FALSE;
    }
    if (g_v6[slot].no_v6) return FALSE;
    g_v6[slot].refusals += weight;
    if (g_v6[slot].refusals < PB_NO_V6_AFTER) return FALSE;
    g_v6[slot].no_v6 = TRUE;
    return TRUE;
}

BOOL pb_proxy_note_ipv6(UINT32 config_id, BOOL refused)
{
    if (config_id == 0) return FALSE;
    AcquireSRWLockExclusive(&g_v6_lock);
    BOOL marked = note_ipv6_locked(config_id, refused, 1);
    ReleaseSRWLockExclusive(&g_v6_lock);
    return marked;
}

void pb_proxy_ipv6_clear(void)
{
    AcquireSRWLockExclusive(&g_v6_lock);
    memset(g_v6, 0, sizeof(g_v6));
    InterlockedIncrement(&g_v6_gen);
    ReleaseSRWLockExclusive(&g_v6_lock);
}

static void log_no_ipv6(const PROXY_CONFIG *p, const char *how)
{
    log_message("Proxy #%u %s:%u has no IPv6 (%s): IPv6 connections through it are refused at once from now on, so programs use IPv4",
        p->config_id, p->host, p->port, how);
}

// Google Public DNS over IPv6, port 443: answers wherever IPv6 works.
static const UINT8 k_probe_v6[16] = {0x20,0x01,0x48,0x60,0x48,0x60,0,0,0,0,0,0,0,0,0x88,0x88};

static DWORD WINAPI ipv6_probe_thread(LPVOID arg)
{
    LONG gen = (LONG)(LONG_PTR)arg;
    UINT32 ids[MAX_PROXY_CONFIGS];
    int n = 0;
    AcquireSRWLockShared(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count && n < MAX_PROXY_CONFIGS; i++)
        ids[n++] = g_proxy_configs[i].config_id;
    ReleaseSRWLockShared(&g_proxy_lock);

    for (int i = 0; i < n; i++)
    {
        // Every registered proxy: the GUI adds the rules after Start, so "referenced" is not known yet.
        PROXY_CONFIG p;
        if (!pb_proxy_snapshot(ids[i], &p) || p.host[0] == '\0' || p.port == 0) continue;
        UINT32 ip = p.resolved_ip ? p.resolved_ip : resolve_hostname(p.host);
        SOCKET s = ip ? socket(AF_INET, SOCK_STREAM, 0) : INVALID_SOCKET;
        if (s == INVALID_SOCKET) { SecureZeroMemory(p.password, sizeof(p.password)); continue; }
        configure_tcp_socket(s, 65536, 8000);
        struct sockaddr_in a;
        memset(&a, 0, sizeof(a));
        a.sin_family = AF_INET;
        a.sin_addr.s_addr = ip;
        a.sin_port = htons(p.port);
        int rc = -1;
        BOOL answered = FALSE;
        if (connect_with_timeout(s, (struct sockaddr *)&a, sizeof(a), 8000) != SOCKET_ERROR)
        {
            pb_set_connect_refused(FALSE);
            rc = p.type == PROXY_TYPE_HTTP ? http_connect_v6(s, k_probe_v6, 443, &p) : socks5_connect_v6(s, k_probe_v6, 443, &p);
            answered = rc == 0 || pb_last_connect_refused();
        }
        closesocket(s);
        SecureZeroMemory(p.password, sizeof(p.password));
        if (!answered) continue;   // dead or slow proxy: real traffic decides later

        AcquireSRWLockExclusive(&g_v6_lock);
        BOOL marked = FALSE;
        if (gen == g_v6_gen)
            marked = note_ipv6_locked(p.config_id, rc != 0, PB_NO_V6_AFTER);
        ReleaseSRWLockExclusive(&g_v6_lock);
        if (marked)
            log_no_ipv6(&p, "checked at connect");
        else if (rc == 0)
            log_message("Proxy #%u %s:%u passes IPv6", p.config_id, p.host, p.port);
    }
    return 0;
}

void pb_proxy_ipv6_probe_async(void)
{
    HANDLE th = CreateThread(NULL, 0, ipv6_probe_thread, (LPVOID)(LONG_PTR)g_v6_gen, 0, NULL);
    if (th) CloseHandle(th);
}

// Turns an outbound IPv6 SYN into the RST+ACK the destination would send for a closed
// port and points it back at the program, which then sees "connection refused" at once.
BOOL pb_tcp_reject_syn_v6(unsigned char *packet, UINT *packet_len, PWINDIVERT_IPV6HDR ip6, PWINDIVERT_TCPHDR tcp, WINDIVERT_ADDRESS *addr)
{
    if (packet == NULL || ip6 == NULL || tcp == NULL || !tcp->Syn || tcp->Ack) return FALSE;
    if ((unsigned char *)tcp != packet + sizeof(WINDIVERT_IPV6HDR)) return FALSE;   // extension headers: leave it
    UINT32 seq = ntohl(tcp->SeqNum);
    UINT32 tmp[4];
    memcpy(tmp, ip6->DstAddr, 16);
    memcpy(ip6->DstAddr, ip6->SrcAddr, 16);
    memcpy(ip6->SrcAddr, tmp, 16);
    UINT16 port = tcp->SrcPort;
    tcp->SrcPort = tcp->DstPort;
    tcp->DstPort = port;
    tcp->SeqNum = 0;
    tcp->AckNum = htonl(seq + 1);
    tcp->HdrLength = 5;
    tcp->Fin = 0; tcp->Syn = 0; tcp->Rst = 1; tcp->Psh = 0; tcp->Ack = 1; tcp->Urg = 0;
    tcp->Window = 0;
    tcp->UrgPtr = 0;
    ip6->Length = htons((UINT16)sizeof(WINDIVERT_TCPHDR));
    *packet_len = (UINT)(sizeof(WINDIVERT_IPV6HDR) + sizeof(WINDIVERT_TCPHDR));
    addr->Outbound = FALSE;
    return TRUE;
}

// OpenDNS answers plain DNS over TCP on port 443 as well as 53; port 443 passes through
// proxies that only allow web ports. Network byte order.
static const UINT32 k_dns443[2] = { 0xDEDE43D0 /* 208.67.222.222 */, 0xDCDC43D0 /* 208.67.220.220 */ };
static volatile LONG g_dns443_next = 0;

static DWORD WINAPI dns_proxy_worker(LPVOID arg)
{
    DNS_JOB *job = (DNS_JOB *)arg;
    unsigned char answer[4096];
    int n = 0;
    UINT32 resolver = pb_is_private_ipv4(job->resolver_ip) ? g_dns_server_ip : job->resolver_ip;
    const char *how = "via proxy";
    BOOL ok = FALSE;
    // The proxy did not answer a moment ago and programs without a rule go direct: do not
    // make every lookup wait for it again.
    BOOL regular_allowed = pb_others_go_direct();
    BOOL dead = regular_allowed && pb_dns_proxy_dead(job->cfg.config_id, GetTickCount64());
    if (!dead && !pb_dns_proxy_broken(job->cfg.config_id, GetTickCount64()))
    {
        ok = pb_dns_tcp_exchange(&job->cfg, resolver, job->query, job->query_len, answer, sizeof(answer), &n);
        if (!ok)
        {
            pb_dns_proxy_mark_broken(job->cfg.config_id, GetTickCount64());
            char key[64];
            snprintf(key, sizeof(key), "dns53:%u", job->cfg.config_id);
            pb_log_ratelimited(key, "[DNS] proxy #%u does not pass DNS on port 53: for the next %d s its queries go to OpenDNS on port 443 through the same proxy",
                job->cfg.config_id, DNS_BROKEN_MS / 1000);
        }
    }
    if (!ok && !dead)
    {
        how = "via proxy (OpenDNS, port 443)";
        LONG first = InterlockedIncrement(&g_dns443_next) & 1;
        for (int i = 0; i < 2 && !ok; i++)
            ok = pb_dns_tcp_exchange_port(&job->cfg, k_dns443[(first + i) & 1], 443, job->query, job->query_len, answer, sizeof(answer), &n);
    }
    if (!ok && regular_allowed)
    {
        if (!dead)
        {
            pb_dns_proxy_mark_dead(job->cfg.config_id, GetTickCount64());
            char key[64];
            snprintf(key, sizeof(key), "dnsdead:%u", job->cfg.config_id);
            pb_log_ratelimited(key, "[DNS] proxy #%u cannot be reached from this network: for the next %d s name lookups are answered by the regular DNS, so programs without a rule stay online; programs with a rule have no connection",
                job->cfg.config_id, DNS_DEAD_MS / 1000);
        }
        how = "by the regular DNS (the proxy is unreachable)";
        ok = dns_udp_direct(job->resolver_ip, job->query, job->query_len, answer, sizeof(answer), &n);
        if (!ok && job->resolver_ip != g_dns_server_ip)
            ok = dns_udp_direct(g_dns_server_ip, job->query, job->query_len, answer, sizeof(answer), &n);
    }
    if (ok)
    {
        snoop_dns_response(answer, n);
        int sent = -1;
        if (udp_relay_socket != INVALID_SOCKET)
            sent = sendto(udp_relay_socket, (const char *)answer, n, 0, (struct sockaddr *)&job->client, sizeof(job->client));
        char key[64];
        snprintf(key, sizeof(key), "dnsok:%u", job->cfg.config_id);
        pb_log_ratelimited(key, "[DNS] answered %d bytes %s (proxy #%u) to client port %u (sendto %d, WSA %d)",
            n, how, job->cfg.config_id, ntohs(job->client.sin_port), sent, sent == SOCKET_ERROR ? WSAGetLastError() : 0);
    }
    else
    {
        char key[64];
        snprintf(key, sizeof(key), "dnsfail:%u", job->cfg.config_id);
        pb_log_ratelimited(key, "[DNS] query through proxy #%u failed on port 53 and on port 443%s", job->cfg.config_id,
            regular_allowed ? " and the regular DNS did not answer either" : "; not sent around the proxy (no leak)");
    }
    SecureZeroMemory(job->cfg.password, sizeof(job->cfg.password));
    free(job);
    InterlockedDecrement(&g_dns_jobs);
    return 0;
}

static void dns_proxy_submit(const PROXY_CONFIG *cfg, UINT32 resolver_ip, const struct sockaddr_in *client,
                             const unsigned char *query, int query_len)
{
    if (query_len <= 0 || query_len > (int)sizeof(((DNS_JOB *)0)->query))
        return;
    if (InterlockedIncrement(&g_dns_jobs) > DNS_MAX_JOBS)
    {
        InterlockedDecrement(&g_dns_jobs);
        pb_log_ratelimited("dnsbusy", "[DNS] too many queries in flight, dropping one");
        return;
    }
    DNS_JOB *job = (DNS_JOB *)calloc(1, sizeof(DNS_JOB));
    if (job == NULL)
    {
        InterlockedDecrement(&g_dns_jobs);
        return;
    }
    job->cfg = *cfg;
    job->resolver_ip = resolver_ip;
    job->client = *client;
    job->query_len = query_len;
    memcpy(job->query, query, query_len);
    HANDLE t = CreateThread(NULL, 0, dns_proxy_worker, job, 0, NULL);
    if (t == NULL)
    {
        SecureZeroMemory(job->cfg.password, sizeof(job->cfg.password));
        free(job);
        InterlockedDecrement(&g_dns_jobs);
        return;
    }
    CloseHandle(t);
}

DWORD WINAPI udp_relay_server(LPVOID arg)
{
    WSADATA wsa_data;
    struct sockaddr_in local_addr = {0}, from_addr = {0};
    unsigned char recv_buf[MAXBUF];
    unsigned char send_buf[MAXBUF];
    int recv_len, from_len = 0;

    if (WSAStartup(MAKEWORD(2, 2), &wsa_data) != 0)
        return 1;

    udp_relay_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (udp_relay_socket == INVALID_SOCKET)
    {
        WSACleanup();
        return 1;
    }

    int on = 1;
    setsockopt(udp_relay_socket, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));
    configure_udp_socket(udp_relay_socket, 262144, 30000);

    memset(&local_addr, 0, sizeof(local_addr));
    local_addr.sin_family = AF_INET;
    local_addr.sin_addr.s_addr = htonl(INADDR_ANY);  // must be any WinDivert swaps src/dst IPs for
    local_addr.sin_port = htons(LOCAL_UDP_RELAY_PORT);// tracked connections so packets arrive at the
                                                      // machines real ip and not 127.0.0.1

    if (bind(udp_relay_socket, (struct sockaddr *)&local_addr, sizeof(local_addr)) == SOCKET_ERROR)
    {
        closesocket(udp_relay_socket);
        udp_relay_socket = INVALID_SOCKET;
        WSACleanup();
        return 1;
    }

    // IPv6 UDP relay socket on ::1:34011
    udp_relay_socket6 = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
    if (udp_relay_socket6 != INVALID_SOCKET)
    {
        int v6only = 1;
        setsockopt(udp_relay_socket6, IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&v6only, sizeof(v6only));
        setsockopt(udp_relay_socket6, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));
        configure_udp_socket(udp_relay_socket6, 262144, 30000);
        struct sockaddr_in6 a6;
        memset(&a6, 0, sizeof(a6));
        a6.sin6_family = AF_INET6;
        a6.sin6_addr = in6addr_any;   // same tracked packets arrive at machines real IPv6
        a6.sin6_port = htons(LOCAL_UDP_RELAY_PORT);
        if (bind(udp_relay_socket6, (struct sockaddr*)&a6, sizeof(a6)) == SOCKET_ERROR)
        {
            closesocket(udp_relay_socket6);
            udp_relay_socket6 = INVALID_SOCKET;
        }
    }

    // Try initial UDP ASSOCIATE only for SOCKS5 configs that an enabled rule actually uses.
    // Skipping unreferenced configs avoids stalling the relay on dead/unused proxies.
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        if (g_proxy_configs[i].type == PROXY_TYPE_SOCKS5 &&
            is_proxy_config_referenced(g_proxy_configs[i].config_id))
        {
            establish_udp_associate_for_config(&g_proxy_configs[i]);
        }
    }

    log_message("UDP relay listening on port %d", LOCAL_UDP_RELAY_PORT);

    while (running)
    {
        // Set whenever an association's sockets are closed and recreated this iteration.
        // Windows reuses closed SOCKET handle values, so the current read_fds (from the
        // select() below) can falsely report the NEW socket ready -> recvfrom on it
        // returns WSAEINVAL (or, on the blocking send socket, stalls ~30s). We must
        // restart the loop and rebuild read_fds before inspecting the new sockets (#183).
        int assoc_replaced = 0;

        fd_set read_fds;
        FD_ZERO(&read_fds);
        FD_SET(udp_relay_socket, &read_fds);
        if (udp_relay_socket6 != INVALID_SOCKET)
            FD_SET(udp_relay_socket6, &read_fds);

        // Add all SOCKS5 configs' TCP control and UDP send sockets
        for (int i = 0; i < g_proxy_config_count; i++)
        {
            PROXY_CONFIG *cfg = &g_proxy_configs[i];
            if (cfg->type != PROXY_TYPE_SOCKS5) continue;
            if (cfg->udp_connected && cfg->udp_tcp_ctrl != INVALID_SOCKET)
                FD_SET(cfg->udp_tcp_ctrl, &read_fds);
            if (cfg->udp_connected && cfg->udp_send_sock != INVALID_SOCKET)
                FD_SET(cfg->udp_send_sock, &read_fds);
        }

        struct timeval timeout = {1, 0};
        if (select(0, &read_fds, NULL, NULL, &timeout) <= 0)
        {
            // Select timed out proactively reconnect any dropped UDP ASSOCIATEs so
            // the connection is ready before the next client packet arrives.
            // Real time communication need real time packet transfer, a single UDP Associate connction can take 1 to 2 seconds and it break the UDP steam for client app
            // fuck you udp this cause slight increase in performance but needed for udp
            for (int i = 0; i < g_proxy_config_count; i++)
            {
                PROXY_CONFIG *rc = &g_proxy_configs[i];
                if (rc->type == PROXY_TYPE_SOCKS5 && !rc->udp_connected &&
                    is_proxy_config_referenced(rc->config_id))
                    establish_udp_associate_for_config(rc);
            }
            continue;
        }

        // Check if any SOCKS5 proxy TCP control socket disconnected
        for (int i = 0; i < g_proxy_config_count; i++)
        {
            PROXY_CONFIG *cfg = &g_proxy_configs[i];
            if (cfg->type != PROXY_TYPE_SOCKS5 || !cfg->udp_connected) continue;
            if (cfg->udp_tcp_ctrl != INVALID_SOCKET && FD_ISSET(cfg->udp_tcp_ctrl, &read_fds))
            {
                char test_buf[1];
                int result = recv(cfg->udp_tcp_ctrl, test_buf, sizeof(test_buf), MSG_PEEK);
                if (result == 0 || (result == SOCKET_ERROR && WSAGetLastError() != WSAEWOULDBLOCK))
                {
                    log_message("[UDP RELAY] TCP control connection closed for proxy %s:%d - reconnecting", cfg->host, cfg->port);
                    closesocket(cfg->udp_tcp_ctrl);
                    cfg->udp_tcp_ctrl = INVALID_SOCKET;
                    if (cfg->udp_send_sock != INVALID_SOCKET)
                    {
                        closesocket(cfg->udp_send_sock);
                        cfg->udp_send_sock = INVALID_SOCKET;
                    }
                    cfg->udp_connected = FALSE;
                    // Reconnect immediately so the next client packet is not dropped.
                    establish_udp_associate_for_config(cfg);
                    assoc_replaced = 1;   // sockets replaced - read_fds is now stale
                }
            }
        }
        if (assoc_replaced) continue;   // rebuild read_fds before touching new sockets

        // Check if packet is from local application
        if (FD_ISSET(udp_relay_socket, &read_fds))
        {
            from_len = sizeof(from_addr);
            recv_len = recvfrom(udp_relay_socket, (char*)recv_buf, sizeof(recv_buf), 0,
                               (struct sockaddr *)&from_addr, &from_len);

            if (recv_len == SOCKET_ERROR)
            {
                // take the error  unreachable so
                // https://github.com/InterceptSuite/ProxyBridge/issues/89
                // select() does not immediately return readable again, causing a spin.
                continue;
            }

            if (recv_len > 0)
            {
                // Buffer overflow protection
                if (recv_len > MAXBUF - 10) continue;

                UINT16 from_port = ntohs(from_addr.sin_port);
                UINT32 dest_ip;
                UINT16 dest_port;

                if (get_connection(from_port, TRUE, &dest_ip, &dest_port))
                {
                    UINT32 proxy_config_id = get_connection_proxy_id(from_port, TRUE);
                    PROXY_CONFIG *cfg = find_proxy_config(proxy_config_id);

                    // DNS goes as DNS over TCP through the proxy (works with HTTP proxies and
                    // with SOCKS5 proxies that refuse UDP ASSOCIATE).
                    if (dest_port == 53 && g_dns_via_proxy)
                    {
                        pb_log_ratelimited("dnsin", "[DNS] query from client port %u (%d bytes) -> resolver %d.%d.%d.%d via proxy #%u",
                            from_port, recv_len, dest_ip & 0xFF, (dest_ip >> 8) & 0xFF, (dest_ip >> 16) & 0xFF, (dest_ip >> 24) & 0xFF,
                            cfg ? cfg->config_id : 0);
                        // The datagram arrived "from" its original destination (the packet loop swaps
                        // the addresses); the answer must go to the application's real address.
                        struct sockaddr_in client = from_addr;
                        UINT32 app_ip = 0;
                        if (get_connection_src_ip(from_port, TRUE, &app_ip))
                            client.sin_addr.s_addr = app_ip;
                        if (cfg != NULL)
                            dns_proxy_submit(cfg, dest_ip, &client, recv_buf, recv_len);
                        continue;
                    }

                    if (cfg == NULL || cfg->type != PROXY_TYPE_SOCKS5)
                    {
                        pb_log_ratelimited("udpnosocks", "[UDP RELAY] UDP for a non-SOCKS5 proxy dropped (port %d)", from_port);
                        continue;
                    }

                    // UDP ASSOCIATE is established (reconnect if dropped).
                    // If reconnect succeeds, fall through and send the current packet
                    // immediately so real-time streams lose at most one packet.
                    if (!cfg->udp_connected)
                    {
                        if (!establish_udp_associate_for_config(cfg))
                        {
                            char key[64];
                            snprintf(key, sizeof(key), "udpassoc:%u", cfg->config_id);
                            pb_log_ratelimited(key, "[UDP RELAY] proxy #%u does not accept UDP ASSOCIATE: its UDP is dropped (no leak)", cfg->config_id);
                            continue;
                        }
                        assoc_replaced = 1;   // new sockets created - read_fds is stale
                    }

                    send_buf[0] = 0;
                    send_buf[1] = 0;
                    send_buf[2] = 0;
                    send_buf[3] = SOCKS5_ATYP_IPV4;
                    send_buf[4] = (dest_ip >> 0) & 0xFF;
                    send_buf[5] = (dest_ip >> 8) & 0xFF;
                    send_buf[6] = (dest_ip >> 16) & 0xFF;
                    send_buf[7] = (dest_ip >> 24) & 0xFF;
                    send_buf[8] = (dest_port >> 8) & 0xFF;
                    send_buf[9] = (dest_port >> 0) & 0xFF;
                    memcpy(&send_buf[10], recv_buf, recv_len);

                    int sent = sendto(cfg->udp_send_sock, (char*)send_buf, 10 + recv_len, 0,
                          (struct sockaddr *)&cfg->udp_relay_addr, sizeof(cfg->udp_relay_addr));

                    if (sent == SOCKET_ERROR) {
                        int err = WSAGetLastError();
                        log_message("[UDP RELAY ERROR] sendto proxy %s:%d failed: %d - reconnecting and retrying", cfg->host, cfg->port, err);
                        if (cfg->udp_tcp_ctrl != INVALID_SOCKET) { closesocket(cfg->udp_tcp_ctrl); cfg->udp_tcp_ctrl = INVALID_SOCKET; }
                        if (cfg->udp_send_sock != INVALID_SOCKET) { closesocket(cfg->udp_send_sock); cfg->udp_send_sock = INVALID_SOCKET; }
                        cfg->udp_connected = FALSE;
                        // Reconnect and retry the current packet so real-time streams
                        // lose at most one packet during a proxy reconnect event.
                        if (establish_udp_associate_for_config(cfg))
                        {
                            sendto(cfg->udp_send_sock, (char*)send_buf, 10 + recv_len, 0,
                                   (struct sockaddr *)&cfg->udp_relay_addr, sizeof(cfg->udp_relay_addr));
                        }
                        assoc_replaced = 1;   // sockets replaced - read_fds is stale
                    }
                }
            }
        }
        if (assoc_replaced) continue;   // rebuild read_fds before inspecting new sockets

        // Check if packet is from any SOCKS5 proxy's UDP socket
        for (int i = 0; i < g_proxy_config_count; i++)
        {
            PROXY_CONFIG *cfg = &g_proxy_configs[i];
            if (cfg->type != PROXY_TYPE_SOCKS5 || !cfg->udp_connected) continue;
            if (cfg->udp_send_sock == INVALID_SOCKET) continue;
            // If not signalled by the outer select, do a zero-timeout check for
            // sockets that were created this iteration (e.g. just after reconnect).
            if (!FD_ISSET(cfg->udp_send_sock, &read_fds))
            {
                fd_set quick;
                FD_ZERO(&quick);
                FD_SET(cfg->udp_send_sock, &quick);
                struct timeval zero_tv = {0, 0};
                if (select(0, &quick, NULL, NULL, &zero_tv) <= 0 || !FD_ISSET(cfg->udp_send_sock, &quick))
                    continue;
            }

            from_len = sizeof(from_addr);
            recv_len = recvfrom(cfg->udp_send_sock, (char*)recv_buf, sizeof(recv_buf), 0,
                               (struct sockaddr *)&from_addr, &from_len);

            if (recv_len == SOCKET_ERROR)
            {
                int err = WSAGetLastError();
                log_message("[UDP RELAY ERROR] Failed to receive from proxy %s:%d: %d - closing", cfg->host, cfg->port, err);
                if (cfg->udp_tcp_ctrl != INVALID_SOCKET) { closesocket(cfg->udp_tcp_ctrl); cfg->udp_tcp_ctrl = INVALID_SOCKET; }
                closesocket(cfg->udp_send_sock);
                cfg->udp_send_sock = INVALID_SOCKET;
                cfg->udp_connected = FALSE;
                continue;
            }

            if (recv_len > 0)
            {
                // Packet from SOCKS5 proxy - decapsulate and forward to original sender
                if (recv_len < 10) continue;

                // SOCKS5 UDP: RSV(2) + FRAG(1) + ATYP(1) + DST.ADDR + DST.PORT(2) + DATA
                if (recv_buf[2] != 0x00) continue;  // FRAG must be 0

                if (recv_buf[3] == SOCKS5_ATYP_IPV4 && recv_len >= 10)
                {
                    UINT32 src_ip = (recv_buf[4]<<0)|(recv_buf[5]<<8)|(recv_buf[6]<<16)|(recv_buf[7]<<24);
                    UINT16 src_port = (recv_buf[8]<<8)|recv_buf[9];

                    BOOL found = FALSE;
                    UINT32 target_ip = 0;
                    UINT16 target_port = 0;
                    CONNECTION_INFO *winner_conn = NULL;

                    AcquireSRWLockShared(&lock);
                    ULONGLONG best_activity = 0;
                    // O(1): only the reverse bucket for this (dest ip, dest port) - not the
                    // whole table - then pick the most-recently-active matching client.
                    for (CONNECTION_INFO *conn = connection_rev_table[rev_hash_v4(src_ip, src_port)];
                         conn != NULL; conn = conn->rev_next)
                    {
                        if (conn->is_udp && !conn->is_ipv6 && conn->orig_dest_ip == src_ip && conn->orig_dest_port == src_port)
                        {
                            if (!found || conn->last_activity > best_activity)
                            {
                                target_ip    = conn->src_ip;
                                target_port  = conn->src_port;
                                best_activity = conn->last_activity;
                                found        = TRUE;
                                winner_conn  = conn;
                                // Do NOT update last_activity here; doing so mid-loop corrupts
                                // best_activity comparisons for later entries. Update after.
                            }
                        }
                    }
                    // Keep winner's session alive (update outside loop so comparisons above
                    // use the original, unmodified timestamps for all candidates).
                    if (winner_conn != NULL)
                        InterlockedExchange64((LONGLONG volatile*)&winner_conn->last_activity, (LONGLONG)GetTickCount64());
                    ReleaseSRWLockShared(&lock);

                    if (found)
                    {
                        struct sockaddr_in target_addr;
                        memset(&target_addr, 0, sizeof(target_addr));
                        target_addr.sin_family = AF_INET;
                        target_addr.sin_addr.s_addr = target_ip;
                        target_addr.sin_port = htons(target_port);
                        int fwd = sendto(udp_relay_socket, (char*)&recv_buf[10], recv_len-10, 0,
                               (struct sockaddr*)&target_addr, sizeof(target_addr));
                        if (fwd == SOCKET_ERROR)
                            log_message("[UDP RELAY] sendto client port %d failed: %d", target_port, WSAGetLastError());
                    }
                    else
                    {
                        log_message("[UDP RELAY] No session found for proxy response from %d.%d.%d.%d:%d - dropped",
                            recv_buf[4], recv_buf[5], recv_buf[6], recv_buf[7], src_port);
                    }
                }
                else if (recv_buf[3] == SOCKS5_ATYP_IPV6 && recv_len >= 22)
                {
                    UINT8 src_ip6[16];
                    memcpy(src_ip6, &recv_buf[4], 16);
                    UINT16 src_port = (recv_buf[20]<<8)|recv_buf[21];

                    UINT8 target_ip6[16];
                    UINT16 target_port = 0;
                    if (find_v6_udp_sender(src_ip6, src_port, target_ip6, &target_port) && udp_relay_socket6 != INVALID_SOCKET)
                    {
                        struct sockaddr_in6 t6;
                        memset(&t6, 0, sizeof(t6));
                        t6.sin6_family = AF_INET6;
                        memcpy(&t6.sin6_addr, target_ip6, 16);
                        t6.sin6_port = htons(target_port);
                        sendto(udp_relay_socket6, (char*)&recv_buf[22], recv_len-22, 0,
                               (struct sockaddr*)&t6, sizeof(t6));
                    }
                }
            }
        }

        // IPv6 UDP packets from application
        if (udp_relay_socket6 != INVALID_SOCKET && FD_ISSET(udp_relay_socket6, &read_fds))
        {
            struct sockaddr_in6 from_addr6 = {0};
            int fl = sizeof(from_addr6);
            recv_len = recvfrom(udp_relay_socket6, (char*)recv_buf, sizeof(recv_buf), 0,
                                (struct sockaddr*)&from_addr6, &fl);
            if (recv_len > 0 && recv_len <= MAXBUF - 22)
            {
                UINT16 from_port = ntohs(from_addr6.sin6_port);
                UINT8  dest_ip6[16];
                UINT16 dest_port = 0;
                UINT32 proxy_config_id = 0;

                if (get_connection_full_v6(from_port, TRUE, dest_ip6, &dest_port, &proxy_config_id))
                {
                    PROXY_CONFIG *cfg = find_proxy_config(proxy_config_id);
                    if (cfg != NULL && cfg->type == PROXY_TYPE_SOCKS5)
                    {
                        if (!cfg->udp_connected) establish_udp_associate_for_config(cfg);
                        if (cfg->udp_connected)
                        {
                            send_buf[0] = 0; send_buf[1] = 0; send_buf[2] = 0;
                            send_buf[3] = SOCKS5_ATYP_IPV6;
                            memcpy(&send_buf[4], dest_ip6, 16);
                            send_buf[20] = (dest_port>>8)&0xFF;
                            send_buf[21] = (dest_port>>0)&0xFF;
                            memcpy(&send_buf[22], recv_buf, recv_len);
                            sendto(cfg->udp_send_sock, (char*)send_buf, 22+recv_len, 0,
                                   (struct sockaddr*)&cfg->udp_relay_addr, sizeof(cfg->udp_relay_addr));
                        }
                    }
                }
            }
        }
    }

    // Clean up all proxy UDP sockets
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        PROXY_CONFIG *cfg = &g_proxy_configs[i];
        if (cfg->udp_tcp_ctrl != INVALID_SOCKET) { closesocket(cfg->udp_tcp_ctrl); cfg->udp_tcp_ctrl = INVALID_SOCKET; }
        if (cfg->udp_send_sock != INVALID_SOCKET) { closesocket(cfg->udp_send_sock); cfg->udp_send_sock = INVALID_SOCKET; }
        cfg->udp_connected = FALSE;
    }
    closesocket(udp_relay_socket);
    udp_relay_socket = INVALID_SOCKET;
    if (udp_relay_socket6 != INVALID_SOCKET) { closesocket(udp_relay_socket6); udp_relay_socket6 = INVALID_SOCKET; }
    WSACleanup();
    return 0;
}

DWORD WINAPI local_proxy_server(LPVOID arg)
{
    WSADATA wsa_data;
    struct sockaddr_in addr;
    SOCKET listen_sock;
    int on = 1;

    if (WSAStartup(MAKEWORD(2, 2), &wsa_data) != 0)
    {
        log_message("WSAStartup failed (%lu)", GetLastError());
        return 1;
    }

    listen_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (listen_sock == INVALID_SOCKET)
    {
        log_message("Socket creation failed (%d)", WSAGetLastError());
        WSACleanup();
        return 1;
    }

    setsockopt(listen_sock, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));

    int nodelay = 1;
    setsockopt(listen_sock, IPPROTO_TCP, TCP_NODELAY, (char*)&nodelay, sizeof(nodelay));

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);  // must be ANY: WinDivert swaps src/dst IPs for
    addr.sin_port = htons(g_local_relay_port); // non-loopback traffic, so redirected SYNs arrive
                                               // at the machine's real IP, not 127.0.0.1

    if (bind(listen_sock, (struct sockaddr *)&addr, sizeof(addr)) == SOCKET_ERROR)
    {
        log_message("Relay cannot bind TCP port %u (WSA %d): another program (or another copy of the core) is using it", g_local_relay_port, WSAGetLastError());
        closesocket(listen_sock);
        WSACleanup();
        return 1;
    }

    if (listen(listen_sock, SOMAXCONN) == SOCKET_ERROR)
    {
        log_message("Relay listen failed (WSA %d)", WSAGetLastError());
        closesocket(listen_sock);
        WSACleanup();
        return 1;
    }

    // IPv6 loopback listener for redirected IPv6 TCP
    SOCKET listen_sock6 = socket(AF_INET6, SOCK_STREAM, 0);
    if (listen_sock6 != INVALID_SOCKET)
    {
        int v6only = 1;
        setsockopt(listen_sock6, IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&v6only, sizeof(v6only));
        setsockopt(listen_sock6, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));
        setsockopt(listen_sock6, IPPROTO_TCP, TCP_NODELAY, (const char*)&nodelay, sizeof(nodelay));
        struct sockaddr_in6 addr6;
        memset(&addr6, 0, sizeof(addr6));
        addr6.sin6_family = AF_INET6;
        addr6.sin6_addr = in6addr_any;   // same reason as IPv4: accept on any local address
        addr6.sin6_port = htons(g_local_relay_port);
        if (bind(listen_sock6, (struct sockaddr*)&addr6, sizeof(addr6)) == SOCKET_ERROR ||
            listen(listen_sock6, SOMAXCONN) == SOCKET_ERROR)
        {
            log_message("IPv6 listen failed (%d)", WSAGetLastError());
            closesocket(listen_sock6);
            listen_sock6 = INVALID_SOCKET;
        }
        else
        {
            log_message("Local proxy IPv6 listening on [::]:%d", g_local_relay_port);
        }
    }

    log_message("Relay listening on 0.0.0.0:%d", g_local_relay_port);
    if (g_relay_ready_event != NULL)
        SetEvent(g_relay_ready_event);   // Start waits for this before diverting packets

    while (running)
    {
        fd_set read_fds;
        FD_ZERO(&read_fds);
        FD_SET(listen_sock, &read_fds);
        if (listen_sock6 != INVALID_SOCKET)
            FD_SET(listen_sock6, &read_fds);
        struct timeval timeout = {1, 0};

        if (select(0, &read_fds, NULL, NULL, &timeout) <= 0)
            continue;

        if (FD_ISSET(listen_sock, &read_fds))
        {
            struct sockaddr_in client_addr;
            int addr_len = sizeof(client_addr);
            SOCKET client_sock = accept(listen_sock, (struct sockaddr *)&client_addr, &addr_len);

            if (client_sock != INVALID_SOCKET)
            {
                CONNECTION_CONFIG *conn_config = (CONNECTION_CONFIG *)malloc(sizeof(CONNECTION_CONFIG));
                if (conn_config != NULL)
                {
                    conn_config->client_socket = client_sock;
                    conn_config->is_ipv6 = FALSE;

                    UINT16 client_port = ntohs(client_addr.sin_port);
                    // A redirected connection arrives "from" its original destination
                    // (the packet loop swaps the addresses). Anything else is a stranger
                    // connecting to the relay port directly and must not get a tunnel.
                    if (get_connection_full(client_port, FALSE, &conn_config->orig_dest_ip, &conn_config->orig_dest_port, &conn_config->proxy_config_id) &&
                        (client_addr.sin_addr.s_addr == conn_config->orig_dest_ip ||
                         (client_addr.sin_addr.s_addr & 0xFF) == 127))
                    {
                        HANDLE conn_thread = CreateThread(NULL, 1, connection_handler, (LPVOID)conn_config, 0, NULL);
                        if (conn_thread != NULL) { CloseHandle(conn_thread); }
                        else { closesocket(client_sock); free(conn_config); }
                    }
                    else { closesocket(client_sock); free(conn_config); }
                }
                else { closesocket(client_sock); }
            }
        }

        if (listen_sock6 != INVALID_SOCKET && FD_ISSET(listen_sock6, &read_fds))
        {
            struct sockaddr_in6 client_addr6;
            int addr_len6 = sizeof(client_addr6);
            SOCKET client_sock6 = accept(listen_sock6, (struct sockaddr*)&client_addr6, &addr_len6);

            if (client_sock6 != INVALID_SOCKET)
            {
                CONNECTION_CONFIG *conn_config = (CONNECTION_CONFIG *)malloc(sizeof(CONNECTION_CONFIG));
                if (conn_config != NULL)
                {
                    conn_config->client_socket = client_sock6;
                    conn_config->is_ipv6 = TRUE;

                    UINT16 client_port = ntohs(client_addr6.sin6_port);
                    if (get_connection_full_v6(client_port, FALSE, conn_config->orig_dest_ip6, &conn_config->orig_dest_port, &conn_config->proxy_config_id) &&
                        (memcmp(&client_addr6.sin6_addr, conn_config->orig_dest_ip6, 16) == 0 ||
                         IN6_IS_ADDR_LOOPBACK(&client_addr6.sin6_addr)))
                    {
                        HANDLE conn_thread = CreateThread(NULL, 1, connection_handler, (LPVOID)conn_config, 0, NULL);
                        if (conn_thread != NULL) { CloseHandle(conn_thread); }
                        else { closesocket(client_sock6); free(conn_config); }
                    }
                    else { closesocket(client_sock6); free(conn_config); }
                }
                else { closesocket(client_sock6); }
            }
        }
    }

    closesocket(listen_sock);
    if (listen_sock6 != INVALID_SOCKET) closesocket(listen_sock6);
    WSACleanup();
    return 0;
}

static void format_dest(BOOL is_ipv6, UINT32 ip4, const UINT8 *ip6, UINT16 port, char *out, size_t out_size)
{
    char ip[64];
    if (is_ipv6)
    {
        inet_ntop(AF_INET6, (void *)ip6, ip, sizeof(ip));
        snprintf(out, out_size, "[%s]:%u", ip, port);
    }
    else
    {
        format_ip_address(ip4, ip, sizeof(ip));
        snprintf(out, out_size, "%s:%u", ip, port);
    }
}

DWORD WINAPI connection_handler(LPVOID arg)
{
    CONNECTION_CONFIG *config = (CONNECTION_CONFIG *)arg;
    SOCKET client_sock = config->client_socket;
    UINT32 dest_ip = config->orig_dest_ip;
    UINT16 dest_port = config->orig_dest_port;
    UINT32 proxy_config_id = config->proxy_config_id;
    BOOL is_ipv6 = config->is_ipv6;
    UINT8 dest_ip6[16];
    memset(dest_ip6, 0, sizeof(dest_ip6));
    if (is_ipv6) memcpy(dest_ip6, config->orig_dest_ip6, 16);
    SOCKET socks_sock;
    struct sockaddr_in socks_addr;
    char dest_str[80];
    char key[64];

    free(config);
    format_dest(is_ipv6, dest_ip, dest_ip6, dest_port, dest_str, sizeof(dest_str));

    // Copy the proxy settings: the GUI may edit or clear the proxy table while this
    // connection is being set up.
    PROXY_CONFIG proxy_copy;
    PROXY_CONFIG *proxy = &proxy_copy;
    if (!pb_proxy_snapshot(proxy_config_id, &proxy_copy) || proxy->host[0] == '\0' || proxy->port == 0)
    {
        snprintf(key, sizeof(key), "noproxy:%u", proxy_config_id);
        pb_log_ratelimited(key, "[RELAY] Proxy #%u is not registered, dropping connection to %s", proxy_config_id, dest_str);
        closesocket(client_sock);
        return 1;
    }

    // Connect to proxy, use cached resolved IP to avoid DNS per connection
    UINT32 proxy_ip = proxy->resolved_ip ? proxy->resolved_ip : resolve_hostname(proxy->host);
    if (proxy_ip == 0)
    {
        snprintf(key, sizeof(key), "resolve:%u", proxy->config_id);
        pb_log_ratelimited(key, "[RELAY] Cannot resolve proxy #%u host %s", proxy->config_id, proxy->host);
        SecureZeroMemory(proxy_copy.password, sizeof(proxy_copy.password));
        closesocket(client_sock);
        return 1;
    }

    socks_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (socks_sock == INVALID_SOCKET)
    {
        log_message("[RELAY] Socket creation failed (WSA %d)", WSAGetLastError());
        SecureZeroMemory(proxy_copy.password, sizeof(proxy_copy.password));
        closesocket(client_sock);
        return 0;
    }

    // 4 MB kernel socket buffers for the relay sockets.
    // The upload path writes from client to proxy over a real network with non-zero
    // RTT; a small (512 KB) send buffer causes send_all() to block the moment
    // the proxy's receive window fills up, which stalls the relay loop and
    // triggers TCP flow-control on the client side, losing upload throughput.
    configure_tcp_socket(socks_sock, 4194304, 30000);  // 4 MB, proxy connection
    configure_tcp_socket(client_sock, 4194304, 30000); // 4 MB, app connection

    memset(&socks_addr, 0, sizeof(socks_addr));
    socks_addr.sin_family = AF_INET;
    socks_addr.sin_addr.s_addr = proxy_ip;
    socks_addr.sin_port = htons(proxy->port);

    // Bounded connect so a dead proxy fails in 10 s instead of the OS SYN timeout.
    if (connect_with_timeout(socks_sock, (struct sockaddr *)&socks_addr, sizeof(socks_addr), 10000) == SOCKET_ERROR)
    {
        int err = WSAGetLastError();
        char pip[32];
        format_ip_address(proxy_ip, pip, sizeof(pip));
        snprintf(key, sizeof(key), "connect:%u:%d", proxy->config_id, err);
        pb_log_ratelimited(key, "[RELAY] Failed to connect to proxy #%u %s:%u (%s) for %s: WSA error %d%s",
            proxy->config_id, proxy->host, proxy->port, pip, dest_str, err,
            err == WSAETIMEDOUT ? " (timed out)" :
            err == WSAECONNREFUSED ? " (connection refused)" :
            err == WSAENETUNREACH ? " (network unreachable)" :
            err == WSAEHOSTUNREACH ? " (host unreachable)" : "");
        SecureZeroMemory(proxy_copy.password, sizeof(proxy_copy.password));
        closesocket(client_sock);
        closesocket(socks_sock);
        return 0;
    }

    int rc = 0;
    pb_set_connect_refused(FALSE);
    if (proxy->type == PROXY_TYPE_SOCKS5)
    {
        char cached_domain[256];
        // Per-config: only hand the hostname to the proxy (socks5h) when this config
        // opts in; otherwise send the locally-resolved IP (socks5).
        if (is_ipv6)
        {
            if (proxy->send_domain_to_proxy && dns_cache_lookup_v6(dest_ip6, cached_domain, sizeof(cached_domain)))
                rc = socks5_connect_domain(socks_sock, cached_domain, dest_port, proxy);
            else
                rc = socks5_connect_v6(socks_sock, dest_ip6, dest_port, proxy);
        }
        else
        {
            if (proxy->send_domain_to_proxy && dns_cache_lookup(dest_ip, cached_domain, sizeof(cached_domain)))
                rc = socks5_connect_domain(socks_sock, cached_domain, dest_port, proxy);
            else
                rc = socks5_connect(socks_sock, dest_ip, dest_port, proxy);
        }
    }
    else if (proxy->type == PROXY_TYPE_HTTP)
    {
        rc = is_ipv6
            ? http_connect_v6(socks_sock, dest_ip6, dest_port, proxy)
            : http_connect(socks_sock, dest_ip, dest_port, proxy);
    }
    if (is_ipv6)
    {
        if (rc == 0)
            pb_proxy_note_ipv6(proxy->config_id, FALSE);
        else if (pb_last_connect_refused() && pb_proxy_note_ipv6(proxy->config_id, TRUE))
            log_no_ipv6(proxy, "it refused IPv6 destinations");
    }
    SecureZeroMemory(proxy_copy.password, sizeof(proxy_copy.password));

    if (rc != 0)
    {
        // The handshake function already logged the protocol detail (reply/status code).
        snprintf(key, sizeof(key), "hs:%u", proxy->config_id);
        pb_log_ratelimited(key, "[RELAY] %s handshake with proxy #%u %s:%u failed for %s (WSA %d)",
            proxy->type == PROXY_TYPE_HTTP ? "HTTP CONNECT" : "SOCKS5",
            proxy->config_id, proxy->host, proxy->port, dest_str, WSAGetLastError());
        closesocket(client_sock);
        closesocket(socks_sock);
        return 0;
    }

    // Disable timeout for data transfer phase
    DWORD zero_timeout = 0;
    setsockopt(socks_sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&zero_timeout, sizeof(zero_timeout));
    setsockopt(socks_sock, SOL_SOCKET, SO_SNDTIMEO, (char*)&zero_timeout, sizeof(zero_timeout));
    setsockopt(client_sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&zero_timeout, sizeof(zero_timeout));
    setsockopt(client_sock, SOL_SOCKET, SO_SNDTIMEO, (char*)&zero_timeout, sizeof(zero_timeout));

    // Enable and configure customized TCP keep-alives
    struct tcp_keepalive keepalive_settings;
    keepalive_settings.onoff = 1;
    keepalive_settings.keepalivetime = 300000;      // 5 minutes in milliseconds
    keepalive_settings.keepaliveinterval = 1000;    // 1 second interval
    DWORD bytes_returned = 0;
    WSAIoctl(socks_sock, SIO_KEEPALIVE_VALS, &keepalive_settings, sizeof(keepalive_settings), NULL, 0, &bytes_returned, NULL, NULL);
    WSAIoctl(client_sock, SIO_KEEPALIVE_VALS, &keepalive_settings, sizeof(keepalive_settings), NULL, 0, &bytes_returned, NULL, NULL);

    TRANSFER_CONFIG *transfer_config = (TRANSFER_CONFIG *)malloc(sizeof(TRANSFER_CONFIG));

    if (transfer_config == NULL)
    {
        log_message("[RELAY] Memory allocation failed for transfer_config");
        closesocket(client_sock);
        closesocket(socks_sock);
        return 0;
    }

    transfer_config->from_socket = client_sock;
    transfer_config->to_socket = socks_sock;

    // both transfer in current thread
    transfer_handler((LPVOID)transfer_config);

    // Sockets already closed in transfer_handler!

    return 0;
}

// One-directional relay: reads from `from` and writes to `to`.
// Runs as a dedicated thread so upload and download never block each other.
// Uses a shared RELAY_PAIR reference count for safe socket cleanup:
//   - whichever direction finishes first calls shutdown() on both sockets,
//     which causes the sibling thread's recv() to return 0 and exit cleanly.
//   - the last thread to exit (refs drops to 0) closes both sockets and
//     frees the shared RELAY_PAIR.
DWORD WINAPI one_way_relay(LPVOID arg)
{
    ONE_WAY_CONFIG *cfg = (ONE_WAY_CONFIG *)arg;
    RELAY_PAIR *pair = cfg->pair;
    SOCKET from = cfg->from;
    SOCKET to   = cfg->to;
    volatile LONG64 *counter = cfg->counter;
    free(cfg);

    char *buf = (char *)malloc(131072);  // 128 KB per-direction buffer
    if (buf)
    {
        int len;
        while ((len = recv(from, buf, 131072, 0)) > 0)
        {
            if (send_all(to, buf, len) == SOCKET_ERROR)
                break;
            if (counter != NULL)
                InterlockedAdd64(counter, (LONG64)len);
        }
        free(buf);
    }

    // Signal the sibling relay to stop by shutting down both sockets.
    // shutdown() is safe to call from any thread; it just drains/resets the
    // socket without closing the handle, so the other thread's recv() returns 0.
    shutdown(pair->sock_client, SD_BOTH);
    shutdown(pair->sock_proxy,  SD_BOTH);

    // Last thread out closes and frees everything.
    if (InterlockedDecrement(&pair->refs) == 0)
    {
        pb_relay_unregister(pair);
        closesocket(pair->sock_client);
        closesocket(pair->sock_proxy);
        free(pair);
    }

    return 0;
}

// Bidirectional relay: spawns one thread for upload (client->proxy) and runs
// the download (proxy->client) direction in the calling thread.  Blocks until
// both directions have finished so the caller (connection_handler) can return
// cleanly and its thread handle can be closed.
DWORD WINAPI transfer_handler(LPVOID arg)
{
    TRANSFER_CONFIG *config = (TRANSFER_CONFIG *)arg;
    SOCKET sock_client = config->from_socket;
    SOCKET sock_proxy  = config->to_socket;
    free(config);

    RELAY_PAIR *pair = (RELAY_PAIR *)malloc(sizeof(RELAY_PAIR));
    if (!pair)
    {
        closesocket(sock_client);
        closesocket(sock_proxy);
        return 1;
    }
    pair->sock_client = sock_client;
    pair->sock_proxy  = sock_proxy;
    pair->refs        = 2;
    pair->reg_next    = NULL;
    pair->reg_prev    = NULL;

    // Upload: client -> proxy  (dedicated thread - may block on slow proxy send)
    ONE_WAY_CONFIG *up = (ONE_WAY_CONFIG *)malloc(sizeof(ONE_WAY_CONFIG));
    // Download: proxy -> client (runs in this thread - loopback, rarely blocks)
    ONE_WAY_CONFIG *dn = (ONE_WAY_CONFIG *)malloc(sizeof(ONE_WAY_CONFIG));

    if (!up || !dn)
    {
        free(up);
        free(dn);
        free(pair);
        closesocket(sock_client);
        closesocket(sock_proxy);
        return 1;
    }

    up->pair = pair;  up->from = sock_client;  up->to = sock_proxy;   up->counter = &g_bytes_up;
    dn->pair = pair;  dn->from = sock_proxy;   dn->to = sock_client;  dn->counter = &g_bytes_down;

    pb_relay_register(pair);

    // Spawn the upload relay in its own thread.
    HANDLE upload_thread = CreateThread(NULL, 0, one_way_relay, up, 0, NULL);
    if (!upload_thread)
    {
        pb_relay_unregister(pair);
        free(up);
        free(dn);
        free(pair);
        closesocket(sock_client);
        closesocket(sock_proxy);
        return 1;
    }

    // Run the download relay in this thread (blocks until done).
    one_way_relay(dn);

    // Wait for the upload relay thread to finish, then clean up its handle.
    WaitForSingleObject(upload_thread, INFINITE);
    CloseHandle(upload_thread);

    return 0;
}

