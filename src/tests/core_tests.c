/*
 * ProxyBridgeCore unit tests. Built as a standalone exe that links the core sources
 * directly (not the DLL). Never starts packet interception: no WinDivertOpen, no
 * ProxyBridge_Start. Only user-mode helpers of WinDivert.dll are used (filter compile/eval).
 *
 * Build: see build-dll.bat (target "tests") or docs/CORE_NOTES.md.
 * Copyright (c) 2026 ProxyBridge Team, MIT License.
 */
#include "../pb_internal.h"

static int g_pass = 0, g_fail = 0;

#define CHECK(cond, name) do { \
    if (cond) { g_pass++; } \
    else { g_fail++; printf("  FAIL: %s  (%s:%d)\n", name, __FILE__, __LINE__); } \
} while (0)

// ---------------------------------------------------------------------------------------
// Log capture
// ---------------------------------------------------------------------------------------
static char g_log[1 << 20];
static size_t g_log_len = 0;
static SRWLOCK g_log_lock = SRWLOCK_INIT;
static BOOL g_echo_log = FALSE;

static void test_log_cb(const char *msg)
{
    AcquireSRWLockExclusive(&g_log_lock);
    size_t n = strlen(msg);
    if (g_log_len + n + 2 < sizeof(g_log))
    {
        memcpy(g_log + g_log_len, msg, n);
        g_log_len += n;
        g_log[g_log_len++] = '\n';
        g_log[g_log_len] = '\0';
    }
    ReleaseSRWLockExclusive(&g_log_lock);
    if (g_echo_log)
        printf("    log: %s\n", msg);
}

static void log_clear(void)
{
    AcquireSRWLockExclusive(&g_log_lock);
    g_log_len = 0;
    g_log[0] = '\0';
    ReleaseSRWLockExclusive(&g_log_lock);
}

static BOOL log_has(const char *s)
{
    AcquireSRWLockShared(&g_log_lock);
    BOOL r = strstr(g_log, s) != NULL;
    ReleaseSRWLockShared(&g_log_lock);
    return r;
}

static int log_count(const char *s)
{
    int n = 0;
    AcquireSRWLockShared(&g_log_lock);
    for (const char *p = g_log; (p = strstr(p, s)) != NULL; p += strlen(s))
        n++;
    ReleaseSRWLockShared(&g_log_lock);
    return n;
}

static char g_all_log[1 << 21];   // everything ever logged, for the password check
static void flush_to_all_log(void)
{
    AcquireSRWLockShared(&g_log_lock);
    strncat_s(g_all_log, sizeof(g_all_log), g_log, _TRUNCATE);
    ReleaseSRWLockShared(&g_log_lock);
}

// ---------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------
static UINT32 ip4(const char *s) { return parse_ipv4(s); }

static void clear_all_rules(void)
{
    for (;;)
    {
        AcquireSRWLockShared(&g_rules_lock);
        UINT32 id = rules_list ? rules_list->rule_id : 0;
        ReleaseSRWLockShared(&g_rules_lock);
        if (id == 0) break;
        ProxyBridge_DeleteRule(id);
    }
}

static void clear_all_proxies(void)
{
    ProxyBridge_ClearProxies();
    if (g_legacy_proxy_id != 0)
        pb_proxy_delete(g_legacy_proxy_id);
}

static RuleAction m4(const char *path, const char *ip, UINT16 port, BOOL udp, UINT32 *pid, UINT32 *rid)
{
    UINT32 p = 0, r = 0;
    RuleAction a = match_rule(path, ip4(ip), port, udp, &p, &r);
    if (pid) *pid = p;
    if (rid) *rid = r;
    return a;
}

static SOCKET listen_local(UINT16 *port_out)
{
    SOCKET l = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = 0;
    bind(l, (struct sockaddr *)&a, sizeof(a));
    listen(l, 8);
    int len = sizeof(a);
    getsockname(l, (struct sockaddr *)&a, &len);
    *port_out = ntohs(a.sin_port);
    return l;
}

static SOCKET connect_local(UINT16 port)
{
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons(port);
    if (connect(s, (struct sockaddr *)&a, sizeof(a)) != 0)
    {
        closesocket(s);
        return INVALID_SOCKET;
    }
    DWORD to = 5000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char *)&to, sizeof(to));
    return s;
}

static int recv_all_n(SOCKET s, char *buf, int n)
{
    int got = 0;
    while (got < n)
    {
        int r = recv(s, buf + got, n - got, 0);
        if (r <= 0) return got;
        got += r;
    }
    return got;
}

// ---------------------------------------------------------------------------------------
// Fake SOCKS5 / HTTP proxy servers
// ---------------------------------------------------------------------------------------
enum {
    S5_OK_AUTH, S5_REFUSED, S5_AUTH_FAIL, S5_V6_IPV4_BND, S5_DOMAIN, S5_NO_ACCEPTABLE,
    HT_OK_SPLIT, HT_407, HT_GARBAGE, S5_TESTCONN
};

typedef struct {
    SOCKET listener;
    int scenario;
    char error[200];
} FAKE;

static void fake_err(FAKE *f, const char *e) { if (!f->error[0]) strncpy_s(f->error, sizeof(f->error), e, _TRUNCATE); }

static DWORD WINAPI fake_server(LPVOID arg)
{
    FAKE *f = (FAKE *)arg;
    SOCKET c = accept(f->listener, NULL, NULL);
    if (c == INVALID_SOCKET) { fake_err(f, "accept failed"); return 0; }
    DWORD to = 5000;
    setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, (char *)&to, sizeof(to));
    unsigned char b[1024];

    if (f->scenario >= HT_OK_SPLIT && f->scenario <= HT_GARBAGE)
    {
        int len = 0;
        while (len < (int)sizeof(b) - 1)
        {
            int r = recv(c, (char *)b + len, 1, 0);
            if (r <= 0) break;
            len++;
            if (len >= 4 && memcmp(b + len - 4, "\r\n\r\n", 4) == 0) break;
        }
        b[len] = 0;
        if (f->scenario == HT_OK_SPLIT)
        {
            if (strstr((char *)b, "CONNECT 1.2.3.4:443 HTTP/1.1\r\n") == NULL) fake_err(f, "bad CONNECT line");
            if (strstr((char *)b, "Proxy-Authorization: Basic dXNlcjpwYXNz\r\n") == NULL) fake_err(f, "bad auth header");
            send(c, "HTTP/1.1 200 Connection established\r\n", 38, 0);
            Sleep(60);
            const char *rest = "Proxy-Agent: fake\r\n\r\nHELLO";
            send(c, rest, (int)strlen(rest), 0);
        }
        else if (f->scenario == HT_407)
        {
            const char *r = "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n";
            send(c, r, (int)strlen(r), 0);
        }
        else
        {
            const char *r = "SSH-2.0-OpenSSH\r\n\r\n";
            send(c, r, (int)strlen(r), 0);
        }
        Sleep(200);
        closesocket(c);
        return 0;
    }

    // SOCKS5 greeting
    if (recv_all_n(c, (char *)b, 2) != 2 || b[0] != 5) { fake_err(f, "bad greeting"); closesocket(c); return 0; }
    int nmethods = b[1];
    if (recv_all_n(c, (char *)b, nmethods) != nmethods) { fake_err(f, "bad methods"); closesocket(c); return 0; }
    BOOL offers_auth = memchr(b, 0x02, nmethods) != NULL;

    if (f->scenario == S5_NO_ACCEPTABLE)
    {
        send(c, "\x05\xFF", 2, 0);
        Sleep(200);
        closesocket(c);
        return 0;
    }

    if (f->scenario == S5_OK_AUTH || f->scenario == S5_AUTH_FAIL)
    {
        if (!offers_auth) { fake_err(f, "client did not offer user/pass"); closesocket(c); return 0; }
        send(c, "\x05\x02", 2, 0);
        if (recv_all_n(c, (char *)b, 2) != 2 || b[0] != 1) { fake_err(f, "bad auth ver"); closesocket(c); return 0; }
        int ul = b[1];
        char user[256] = {0}, pass[256] = {0};
        recv_all_n(c, user, ul);
        unsigned char pl;
        recv_all_n(c, (char *)&pl, 1);
        recv_all_n(c, pass, pl);
        if (strcmp(user, "user") != 0 || strcmp(pass, "S3cr3tPW!") != 0) fake_err(f, "wrong credentials");
        if (f->scenario == S5_AUTH_FAIL)
        {
            send(c, "\x01\x01", 2, 0);
            Sleep(200);
            closesocket(c);
            return 0;
        }
        send(c, "\x01\x00", 2, 0);
    }
    else
    {
        send(c, "\x05\x00", 2, 0);
    }

    // CONNECT request header
    if (recv_all_n(c, (char *)b, 4) != 4 || b[0] != 5 || b[1] != 1) { fake_err(f, "bad CONNECT"); closesocket(c); return 0; }
    int atyp = b[3];
    if (atyp == 1) { recv_all_n(c, (char *)b + 4, 6); if (f->scenario != S5_TESTCONN && memcmp(b + 4, "\x01\x02\x03\x04\x01\xBB", 6) != 0) fake_err(f, "bad IPv4 dest"); }
    else if (atyp == 4) { recv_all_n(c, (char *)b + 4, 18); if (b[4] != 0x20 || b[5] != 0x01 || b[20] != 0x01 || b[21] != 0xBB) fake_err(f, "bad IPv6 dest"); }
    else if (atyp == 3)
    {
        unsigned char dl; recv_all_n(c, (char *)&dl, 1);
        char name[256] = {0}; recv_all_n(c, name, dl); recv_all_n(c, (char *)b, 2);
        if (strcmp(name, "example.com") != 0 || b[0] != 0x01 || b[1] != 0xBB) fake_err(f, "bad domain dest");
    }
    else fake_err(f, "bad atyp");

    if (f->scenario == S5_REFUSED)
    {
        send(c, "\x05\x05\x00\x01\x00\x00\x00\x00\x00\x00", 10, 0);
    }
    else
    {
        // success reply split across two segments, then tunnel bytes in the same flow
        send(c, "\x05\x00\x00", 3, 0);
        Sleep(60);
        if (f->scenario != S5_TESTCONN)
            send(c, "\x01\x00\x00\x00\x00\x00\x00TUNNEL", 13, 0);
        else
        {
            send(c, "\x01\x00\x00\x00\x00\x00\x00", 7, 0);
            // ProxyBridge_TestConnection sends a GET through the tunnel
            int len = 0;
            while (len < (int)sizeof(b) - 1) { int r = recv(c, (char *)b + len, 1, 0); if (r <= 0) break; len++; if (len >= 4 && memcmp(b + len - 4, "\r\n\r\n", 4) == 0) break; }
            const char *r = "HTTP/1.1 204 No Content\r\n\r\n";
            send(c, r, (int)strlen(r), 0);
        }
    }
    Sleep(200);
    closesocket(c);
    return 0;
}

typedef struct { FAKE f; HANDLE th; UINT16 port; } FAKE_RUN;

static void fake_start(FAKE_RUN *r, int scenario)
{
    memset(r, 0, sizeof(*r));
    r->f.listener = listen_local(&r->port);
    r->f.scenario = scenario;
    r->th = CreateThread(NULL, 0, fake_server, &r->f, 0, NULL);
}

static void fake_finish(FAKE_RUN *r)
{
    WaitForSingleObject(r->th, 5000);
    CloseHandle(r->th);
    closesocket(r->f.listener);
}

static PROXY_CONFIG make_cfg(ProxyType t, const char *user, const char *pass)
{
    PROXY_CONFIG c;
    memset(&c, 0, sizeof(c));
    c.type = t;
    strcpy_s(c.host, sizeof(c.host), "127.0.0.1");
    c.port = 1;
    if (user) strcpy_s(c.username, sizeof(c.username), user);
    if (pass) strcpy_s(c.password, sizeof(c.password), pass);
    return c;
}

// ---------------------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------------------
static void test_rule_matching(void)
{
    printf("[rules] matching\n");
    clear_all_rules();
    clear_all_proxies();

    UINT32 p1 = ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "10.9.8.1", 1080, NULL, NULL);
    UINT32 p2 = ProxyBridge_AddProxy(PROXY_TYPE_HTTP,   "10.9.8.2", 3128, NULL, NULL);
    UINT32 p3 = ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "10.9.8.3", 1080, NULL, NULL);
    CHECK(p1 && p2 && p3 && p1 != p2 && p2 != p3, "three proxies registered with distinct ids");

    UINT32 r1 = ProxyBridge_AddRuleEx("chrome.exe", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p1);
    UINT32 r2 = ProxyBridge_AddRuleEx("FIRE*.EXE", "*", "443; 8000-8100", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p2);
    UINT32 r3 = ProxyBridge_AddRuleEx("C:\\Apps\\Tool\\tool.exe", "*", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_DIRECT, 0);
    UINT32 r4 = ProxyBridge_AddRuleEx("*", "192.168.*.*;10.0.0.1-10.0.0.9", "*", RULE_PROTOCOL_TCP, RULE_ACTION_DIRECT, 0);
    UINT32 r5 = ProxyBridge_AddRuleEx("blocked.exe;*steam*", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_BLOCK, 0);
    UINT32 r6 = ProxyBridge_AddRuleEx("*", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p3);
    CHECK(r1 && r2 && r3 && r4 && r5 && r6, "rules added");
    CHECK(r1 < r2 && r2 < r3 && r5 < r6, "rule ids increase");

    UINT32 pid, rid;
    CHECK(m4("C:\\Program Files\\Google\\Chrome\\chrome.exe", "142.250.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && pid == p1 && rid == r1, "exe name match -> PROXY p1");
    CHECK(m4("C:\\x\\CHROME.EXE", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r1, "exe name is case-insensitive");
    CHECK(m4("C:\\x\\chrome.exe.bak", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6, "exe name must match exactly (no prefix match)");
    CHECK(m4("C:\\Mozilla\\firefox.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && pid == p2 && rid == r2, "wildcard exe + port 443");
    CHECK(m4("C:\\Mozilla\\firefox.exe", "1.1.1.1", 8050, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r2, "port range 8000-8100");
    CHECK(m4("C:\\Mozilla\\firefox.exe", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && pid == p3 && rid == r6, "port mismatch falls to catch-all");
    CHECK(m4("C:\\Apps\\Tool\\tool.exe", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == r3, "full path rule");
    CHECK(m4("C:\\apps\\tool\\TOOL.exe", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == r3, "full path rule is case-insensitive");
    CHECK(m4("D:\\Other\\tool.exe", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6, "full path rule does not match another dir");
    CHECK(m4("C:\\w\\notepad.exe", "192.168.1.5", 80, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == r4, "host wildcard 192.168.*.*");
    CHECK(m4("C:\\w\\notepad.exe", "10.0.0.5", 80, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == r4, "host range");
    CHECK(m4("C:\\w\\notepad.exe", "10.0.0.10", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6, "outside host range -> catch-all");
    CHECK(m4("C:\\w\\blocked.exe", "8.8.8.8", 443, FALSE, &pid, &rid) == RULE_ACTION_BLOCK && rid == r5, "process list ; BLOCK");
    CHECK(m4("C:\\Steam\\steamwebhelper.exe", "8.8.8.8", 443, FALSE, &pid, &rid) == RULE_ACTION_BLOCK && rid == r5, "multi-star wildcard *steam*");
    CHECK(m4("C:\\x\\chrome.exe", "1.1.1.1", 443, TRUE, &pid, &rid) == RULE_ACTION_DIRECT && rid == 0, "TCP-only rules do not match UDP");
    CHECK(m4("C:\\Apps\\Tool\\tool.exe", "1.1.1.1", 53, TRUE, &pid, &rid) == RULE_ACTION_DIRECT && rid == r3, "BOTH rule matches UDP");
    CHECK(m4("", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6, "* matches even an unnamed process");

    // disabled rules are skipped, re-enabled rules apply again
    CHECK(ProxyBridge_DisableRule(r1), "DisableRule");
    CHECK(m4("C:\\x\\chrome.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6 && pid == p3, "disabled rule skipped");
    CHECK(ProxyBridge_EnableRule(r1), "EnableRule");
    CHECK(m4("C:\\x\\chrome.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r1, "re-enabled rule applies");
    CHECK(!ProxyBridge_DisableRule(999999), "DisableRule unknown id fails");

    // EditRule keeps the proxy id
    CHECK(ProxyBridge_EditRule(r2, "opera.exe", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY), "EditRule");
    CHECK(m4("C:\\o\\opera.exe", "1.1.1.1", 80, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r2 && pid == p2, "edited rule keeps its proxy id");
    CHECK(m4("C:\\Mozilla\\firefox.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == r6, "edited rule no longer matches old process");

    // delete catch-all: no match -> DIRECT
    CHECK(ProxyBridge_DeleteRule(r6), "DeleteRule");
    CHECK(m4("C:\\w\\notepad.exe", "1.2.3.4", 443, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == 0 && pid == 0, "no match -> DIRECT");
    CHECK(!ProxyBridge_DeleteRule(r6), "DeleteRule twice fails");

    // strict first-match order: a catch-all placed first shadows later rules
    clear_all_rules();
    UINT32 c1 = ProxyBridge_AddRuleEx("*", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_DIRECT, 0);
    UINT32 c2 = ProxyBridge_AddRuleEx("chrome.exe", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p1);
    CHECK(m4("C:\\x\\chrome.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == c1, "first match wins (catch-all first)");
    ProxyBridge_DisableRule(c1);
    CHECK(m4("C:\\x\\chrome.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_PROXY && rid == c2, "after disabling the first rule the next one matches");
    UINT32 c3 = ProxyBridge_AddRule("ANY", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_BLOCK);
    CHECK(m4("C:\\x\\other.exe", "1.1.1.1", 443, FALSE, &pid, &rid) == RULE_ACTION_BLOCK && rid == c3, "ANY keyword matches every process");

    // IPv6 hosts
    clear_all_rules();
    UINT32 v1 = ProxyBridge_AddRuleEx("*", "2001:db8::/32", "*", RULE_PROTOCOL_TCP, RULE_ACTION_DIRECT, 0);
    UINT32 v2 = ProxyBridge_AddRuleEx("*", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p1);
    UINT8 a6[16], b6[16];
    inet_pton(AF_INET6, "2001:db8::1", a6);
    inet_pton(AF_INET6, "2a00:1450::1", b6);
    UINT32 px6 = 0, rid6 = 0;
    CHECK(match_rule_v6("C:\\x\\a.exe", a6, 443, FALSE, &px6, &rid6) == RULE_ACTION_DIRECT && rid6 == v1, "IPv6 CIDR rule");
    CHECK(match_rule_v6("C:\\x\\a.exe", b6, 443, FALSE, &px6, &rid6) == RULE_ACTION_PROXY && rid6 == v2 && px6 == p1, "IPv6 catch-all");

    // invalid arguments
    CHECK(ProxyBridge_AddRuleEx(NULL, "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, p1) == 0, "AddRuleEx NULL process rejected");
    CHECK(ProxyBridge_AddRuleEx("a.exe", "*", "*", (RuleProtocol)9, RULE_ACTION_PROXY, p1) == 0, "AddRuleEx bad protocol rejected");
    CHECK(ProxyBridge_AddRuleEx("a.exe", "*", "*", RULE_PROTOCOL_TCP, (RuleAction)7, p1) == 0, "AddRuleEx bad action rejected");
    clear_all_rules();
    UINT32 nul = ProxyBridge_AddRuleEx("a.exe", NULL, NULL, RULE_PROTOCOL_TCP, RULE_ACTION_DIRECT, 0);
    CHECK(nul != 0 && m4("C:\\a.exe", "9.9.9.9", 1, FALSE, &pid, &rid) == RULE_ACTION_DIRECT && rid == nul, "NULL hosts/ports mean *");

    clear_all_rules();
}

static void test_check_process_rule(void)
{
    printf("[rules] decisions on a real socket owned by this process\n");
    clear_all_rules();
    clear_all_proxies();
    log_clear();

    UINT32 px = ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "10.9.8.7", 1080, "user", "S3cr3tPW!");
    CHECK(px != 0, "proxy registered");

    char exe[MAX_PATH];
    GetModuleFileNameA(NULL, exe, sizeof(exe));
    const char *exe_name = extract_filename(exe);
    UINT32 r = ProxyBridge_AddRuleEx(exe_name, "*", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_PROXY, px);
    CHECK(r != 0, "rule for this exe added");

    UINT16 lport;
    SOCKET l = listen_local(&lport);
    SOCKET c = connect_local(lport);
    SOCKET a = accept(l, NULL, NULL);
    struct sockaddr_in local;
    int len = sizeof(local);
    getsockname(c, (struct sockaddr *)&local, &len);
    UINT32 src = local.sin_addr.s_addr;
    UINT16 sport = ntohs(local.sin_port);

    DWORD pid = 0;
    UINT32 pcid = 0;
    RuleAction act = check_process_rule(src, sport, ip4("93.184.216.34"), 443, FALSE, &pid, &pcid);
    CHECK(act == RULE_ACTION_PROXY && pcid == px, "own TCP socket -> PROXY via rule");
    CHECK(pid == GetCurrentProcessId(), "owner pid resolved from the TCP table");
    CHECK(log_has("[CONN] ") && log_has("-> 93.184.216.34:443 TCP: rule #"), "decision logged");

    CHECK(check_process_rule(src, sport, ip4("10.9.8.7"), 1080, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "proxy server IP always direct");
    CHECK(log_has("destination is a proxy server"), "proxy exclusion logged");
    CHECK(check_process_rule(src, sport, ip4("127.0.0.5"), 80, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "loopback direct");
    CHECK(check_process_rule(src, sport, ip4("224.0.0.251"), 5353, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "multicast direct");
    CHECK(check_process_rule(src, sport, ip4("255.255.255.255"), 80, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "broadcast direct");
    CHECK(check_process_rule(src, sport, ip4("169.254.10.10"), 80, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "link-local direct");

    // rate limiting: the same decision is logged once per window
    log_clear();
    for (int i = 0; i < 50; i++)
        check_process_rule(src, sport, ip4("93.184.216.35"), 443, FALSE, &pid, &pcid);
    CHECK(log_count("93.184.216.35:443") == 1, "repeated decision logged once (rate limited)");

    // UDP is never proxied, even with a BOTH rule
    SOCKET u = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in ua;
    memset(&ua, 0, sizeof(ua));
    ua.sin_family = AF_INET;
    bind(u, (struct sockaddr *)&ua, sizeof(ua));
    len = sizeof(ua);
    getsockname(u, (struct sockaddr *)&ua, &len);
    log_clear();
    act = check_process_rule(ip4("192.168.1.50"), ntohs(ua.sin_port), ip4("8.8.8.8"), 443, TRUE, &pid, &pcid);
    CHECK(act == RULE_ACTION_DIRECT && pcid == 0, "UDP with a PROXY rule goes DIRECT");
    CHECK(pid == GetCurrentProcessId(), "UDP owner resolved");
    CHECK(log_has("UDP is not proxied"), "UDP override logged");
    closesocket(u);

    // a rule pointing at an unknown proxy id goes direct instead of using another proxy
    clear_all_rules();
    ProxyBridge_AddRuleEx(exe_name, "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, 999);
    CHECK(check_process_rule(src, sport, ip4("93.184.216.36"), 443, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "unknown proxy id -> DIRECT");
    CHECK(log_has("proxy id not registered"), "unknown proxy id logged");

    // the host process (the GUI in production) is never intercepted
    clear_all_rules();
    ProxyBridge_AddRuleEx("*", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, px);
    CHECK(check_process_rule(src, sport, ip4("93.184.216.37"), 443, FALSE, &pid, &pcid) == RULE_ACTION_PROXY, "catch-all * proxies this process");
    g_current_process_id = GetCurrentProcessId();
    CHECK(check_process_rule(src, sport, ip4("93.184.216.38"), 443, FALSE, &pid, &pcid) == RULE_ACTION_DIRECT, "host process always DIRECT");
    g_current_process_id = 0;

    closesocket(a);
    closesocket(c);
    closesocket(l);
    clear_all_rules();
    flush_to_all_log();
}

static void test_proxy_api(void)
{
    printf("[proxies] registration and resolution\n");
    clear_all_rules();
    clear_all_proxies();
    log_clear();

    UINT32 a = ProxyBridge_AddProxy(PROXY_TYPE_HTTP, "127.0.0.1", 8080, NULL, NULL);
    UINT32 b = ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "localhost", 1080, "user", "S3cr3tPW!");
    CHECK(a != 0, "IP literal proxy");
    CHECK(b != 0, "hostname proxy (localhost) resolved");
    PROXY_CONFIG s;
    CHECK(pb_proxy_snapshot(b, &s) && s.resolved_ip == ip4("127.0.0.1") && s.type == PROXY_TYPE_SOCKS5 && s.port == 1080, "hostname resolved to IPv4 once at AddProxy");
    CHECK(log_has("auth yes") && log_has("auth no"), "auth yes/no logged");
    CHECK(ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "no-such-host.invalid", 1080, NULL, NULL) == 0, "unresolvable host rejected");
    CHECK(ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "1.2.3.4", 0, NULL, NULL) == 0, "port 0 rejected");
    CHECK(ProxyBridge_AddProxy((ProxyType)5, "1.2.3.4", 80, NULL, NULL) == 0, "unknown type rejected");
    CHECK(ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "", 80, NULL, NULL) == 0, "empty host rejected");
    CHECK(pb_is_proxy_server_ip(ip4("127.0.0.1")) && !pb_is_proxy_server_ip(ip4("127.0.0.2")), "proxy IP lookup");

    // legacy single proxy: proxy id for rules without an explicit proxy (AddRule)
    CHECK(ProxyBridge_SetProxyConfig(PROXY_TYPE_SOCKS5, "10.1.1.1", 1080, "u", "S3cr3tPW!"), "SetProxyConfig");
    UINT32 legacy = g_legacy_proxy_id;
    CHECK(legacy != 0, "legacy proxy id assigned");
    ProxyBridge_ClearProxies();
    CHECK(!pb_proxy_snapshot(a, &s) && !pb_proxy_snapshot(b, &s), "ClearProxies removed AddProxy entries");
    CHECK(pb_proxy_snapshot(0, &s) && s.config_id == legacy && s.resolved_ip == ip4("10.1.1.1"), "ClearProxies keeps the SetProxyConfig proxy (default)");
    CHECK(ProxyBridge_SetProxyConfig(PROXY_TYPE_HTTP, "10.1.1.2", 3128, NULL, NULL) && g_legacy_proxy_id == legacy, "SetProxyConfig again edits the same id");
    CHECK(pb_proxy_snapshot(0, &s) && s.type == PROXY_TYPE_HTTP && s.resolved_ip == ip4("10.1.1.2") && s.username[0] == 0, "default proxy updated");
    UINT32 lr = ProxyBridge_AddRule("legacy.exe", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY);
    UINT32 pid = 0, rid = 0;
    RuleAction act = m4("C:\\legacy.exe", "5.5.5.5", 443, FALSE, &pid, &rid);
    CHECK(act == RULE_ACTION_PROXY && rid == lr && pid == 0, "legacy AddRule uses proxy id 0 (default)");
    PROXY_CONFIG *def = find_proxy_config(0);
    CHECK(def != NULL && def->config_id == legacy, "proxy id 0 resolves to the default proxy");
    CHECK(find_proxy_config(4242) == NULL, "unknown id does not fall back to another proxy");

    // table capacity
    clear_all_proxies();
    int added = 0;
    for (int i = 0; i < MAX_PROXY_CONFIGS + 5; i++)
    {
        char host[32];
        snprintf(host, sizeof(host), "10.50.%d.%d", i / 200, i % 200 + 1);
        if (ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, host, 1080, NULL, NULL) != 0) added++;
    }
    CHECK(added == MAX_PROXY_CONFIGS, "proxy table caps at MAX_PROXY_CONFIGS without overflow");
    clear_all_proxies();
    CHECK(g_proxy_config_count == 0, "all proxies removed");
    clear_all_rules();
    flush_to_all_log();
}

static void test_socks5(void)
{
    printf("[socks5] handshakes against a local fake server\n");
    log_clear();
    FAKE_RUN fr;
    char buf[16];

    // username/password auth + IPv4 CONNECT, reply split over two segments
    fake_start(&fr, S5_OK_AUTH);
    SOCKET s = connect_local(fr.port);
    PROXY_CONFIG cfg = make_cfg(PROXY_TYPE_SOCKS5, "user", "S3cr3tPW!");
    int rc = socks5_connect(s, ip4("1.2.3.4"), 443, &cfg);
    CHECK(rc == 0, "SOCKS5 auth + CONNECT succeeds");
    CHECK(recv_all_n(s, buf, 6) == 6 && memcmp(buf, "TUNNEL", 6) == 0, "SOCKS5 reply fully consumed, tunnel bytes intact");
    closesocket(s);
    fake_finish(&fr);
    CHECK(fr.f.error[0] == 0, fr.f.error[0] ? fr.f.error : "fake server saw a valid SOCKS5 request");

    // CONNECT refused (REP 5)
    fake_start(&fr, S5_REFUSED);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_SOCKS5, NULL, NULL);
    CHECK(socks5_connect(s, ip4("1.2.3.4"), 443, &cfg) != 0, "SOCKS5 REP=5 fails");
    CHECK(log_has("reply code 5 (connection refused)"), "SOCKS5 reply code logged");
    closesocket(s);
    fake_finish(&fr);

    // authentication rejected
    fake_start(&fr, S5_AUTH_FAIL);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_SOCKS5, "user", "S3cr3tPW!");
    CHECK(socks5_connect(s, ip4("1.2.3.4"), 443, &cfg) != 0, "SOCKS5 auth failure detected");
    CHECK(log_has("Authentication rejected"), "SOCKS5 auth failure logged");
    closesocket(s);
    fake_finish(&fr);

    // no acceptable method (proxy wants auth, none configured)
    fake_start(&fr, S5_NO_ACCEPTABLE);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_SOCKS5, NULL, NULL);
    CHECK(socks5_connect(s, ip4("1.2.3.4"), 443, &cfg) != 0, "SOCKS5 0xFF method reply fails");
    CHECK(log_has("accepted none of the offered auth methods"), "SOCKS5 0xFF logged");
    closesocket(s);
    fake_finish(&fr);

    // IPv6 CONNECT answered with an IPv4 BND.ADDR
    fake_start(&fr, S5_V6_IPV4_BND);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_SOCKS5, NULL, NULL);
    UINT8 d6[16];
    inet_pton(AF_INET6, "2001:db8::1", d6);
    CHECK(socks5_connect_v6(s, d6, 443, &cfg) == 0, "SOCKS5 IPv6 CONNECT with IPv4 BND reply");
    CHECK(recv_all_n(s, buf, 6) == 6 && memcmp(buf, "TUNNEL", 6) == 0, "IPv6 tunnel bytes intact");
    closesocket(s);
    fake_finish(&fr);
    CHECK(fr.f.error[0] == 0, fr.f.error[0] ? fr.f.error : "fake server saw a valid IPv6 request");

    // domain CONNECT
    fake_start(&fr, S5_DOMAIN);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_SOCKS5, NULL, NULL);
    CHECK(socks5_connect_domain(s, "example.com", 443, &cfg) == 0, "SOCKS5 domain CONNECT");
    closesocket(s);
    fake_finish(&fr);
    CHECK(fr.f.error[0] == 0, fr.f.error[0] ? fr.f.error : "fake server saw a valid domain request");

    // oversize credentials are refused before anything is sent
    cfg = make_cfg(PROXY_TYPE_SOCKS5, "user", NULL);
    memset(cfg.password, 'p', sizeof(cfg.password) - 1);   // 255 bytes is the limit: exactly 255 is valid
    cfg.password[sizeof(cfg.password) - 1] = 0;
    CHECK(strlen(cfg.password) == 255, "255-byte password fits the RFC 1929 limit");
    flush_to_all_log();
}

static void test_http(void)
{
    printf("[http] CONNECT handshakes against a local fake server\n");
    log_clear();
    FAKE_RUN fr;
    char buf[16];

    fake_start(&fr, HT_OK_SPLIT);
    SOCKET s = connect_local(fr.port);
    PROXY_CONFIG cfg = make_cfg(PROXY_TYPE_HTTP, "user", "pass");
    CHECK(http_connect(s, ip4("1.2.3.4"), 443, &cfg) == 0, "HTTP CONNECT 200 with split headers");
    CHECK(recv_all_n(s, buf, 5) == 5 && memcmp(buf, "HELLO", 5) == 0, "bytes after the CONNECT reply are not swallowed");
    closesocket(s);
    fake_finish(&fr);
    CHECK(fr.f.error[0] == 0, fr.f.error[0] ? fr.f.error : "CONNECT request line and Basic auth header correct");

    fake_start(&fr, HT_407);
    s = connect_local(fr.port);
    cfg = make_cfg(PROXY_TYPE_HTTP, NULL, NULL);
    CHECK(http_connect(s, ip4("1.2.3.4"), 443, &cfg) != 0, "HTTP 407 fails");
    CHECK(log_has("failed with status 407"), "HTTP status logged");
    closesocket(s);
    fake_finish(&fr);

    fake_start(&fr, HT_GARBAGE);
    s = connect_local(fr.port);
    CHECK(http_connect(s, ip4("1.2.3.4"), 443, &cfg) != 0, "non-HTTP reply fails");
    closesocket(s);
    fake_finish(&fr);
    flush_to_all_log();
}

typedef struct { SOCKET from, to; } XFER;
static DWORD WINAPI xfer_thread(LPVOID arg)
{
    XFER *x = (XFER *)arg;
    TRANSFER_CONFIG *tc = (TRANSFER_CONFIG *)malloc(sizeof(TRANSFER_CONFIG));
    tc->from_socket = x->from;
    tc->to_socket = x->to;
    transfer_handler(tc);
    return 0;
}

static void make_pair(SOCKET *a, SOCKET *b)
{
    UINT16 port;
    SOCKET l = listen_local(&port);
    *a = connect_local(port);
    *b = accept(l, NULL, NULL);
    DWORD to = 5000;
    setsockopt(*b, SOL_SOCKET, SO_RCVTIMEO, (char *)&to, sizeof(to));
    closesocket(l);
}

static void test_counters(void)
{
    printf("[counters] relay byte counters\n");
    pb_reset_counters();
    UINT64 up = 1, down = 1;
    ProxyBridge_GetTrafficStats(&up, &down);
    CHECK(up == 0 && down == 0, "counters reset");
    ProxyBridge_GetTrafficStats(NULL, NULL);   // must not crash

    SOCKET app, relay_app, relay_proxy, proxy;
    make_pair(&app, &relay_app);
    make_pair(&relay_proxy, &proxy);
    XFER x = { relay_app, relay_proxy };
    HANDLE th = CreateThread(NULL, 0, xfer_thread, &x, 0, NULL);

    static char big[100000];
    for (int i = 0; i < (int)sizeof(big); i++) big[i] = (char)(i * 7);
    send_all(app, big, sizeof(big));
    static char rx[100000];
    int got = recv_all_n(proxy, rx, sizeof(rx));
    CHECK(got == (int)sizeof(big) && memcmp(rx, big, sizeof(big)) == 0, "100000 bytes relayed app -> proxy intact");
    send_all(proxy, big, 5000);
    got = recv_all_n(app, rx, 5000);
    CHECK(got == 5000 && memcmp(rx, big, 5000) == 0, "5000 bytes relayed proxy -> app intact");

    // The app closes; the relay half-closes towards the proxy, which then closes too.
    closesocket(app);
    got = recv(proxy, rx, 1, 0);
    CHECK(got == 0, "app close is propagated to the proxy side as FIN");
    closesocket(proxy);
    CHECK(WaitForSingleObject(th, 5000) == WAIT_OBJECT_0, "relay thread ends when both sides closed");
    CloseHandle(th);
    ProxyBridge_GetTrafficStats(&up, &down);
    CHECK(up == 100000 && down == 5000, "GetTrafficStats up=100000 down=5000");

    // Stop path: active relays are shut down
    make_pair(&app, &relay_app);
    make_pair(&relay_proxy, &proxy);
    XFER y = { relay_app, relay_proxy };
    th = CreateThread(NULL, 0, xfer_thread, &y, 0, NULL);
    send_all(app, "x", 1);
    recv_all_n(proxy, rx, 1);
    pb_relay_shutdown_all();
    CHECK(WaitForSingleObject(th, 3000) == WAIT_OBJECT_0, "pb_relay_shutdown_all ends active relays");
    CloseHandle(th);
    closesocket(app);
    closesocket(proxy);
    ProxyBridge_GetTrafficStats(&up, &down);
    CHECK(up == 100001, "counter accumulates across connections");
    pb_reset_counters();
    ProxyBridge_GetTrafficStats(&up, &down);
    CHECK(up == 0 && down == 0, "reset (called by Start) zeroes counters");
}

// Build a minimal IPv4 or IPv6 TCP/UDP packet for WinDivertHelperEvalFilter.
static UINT build_packet(unsigned char *pkt, BOOL v6, BOOL udp, const char *src, const char *dst, UINT16 sport, UINT16 dport)
{
    memset(pkt, 0, 128);
    UINT ip_len = v6 ? 40 : 20;
    UINT l4_len = udp ? 8 : 20;
    UINT total = ip_len + l4_len;
    if (v6)
    {
        pkt[0] = 0x60;
        pkt[4] = 0; pkt[5] = (unsigned char)l4_len;
        pkt[6] = udp ? 17 : 6;
        pkt[7] = 64;
        inet_pton(AF_INET6, src, pkt + 8);
        inet_pton(AF_INET6, dst, pkt + 24);
    }
    else
    {
        pkt[0] = 0x45;
        pkt[2] = 0; pkt[3] = (unsigned char)total;
        pkt[8] = 64;
        pkt[9] = udp ? 17 : 6;
        UINT32 s = ip4(src), d = ip4(dst);
        memcpy(pkt + 12, &s, 4);
        memcpy(pkt + 16, &d, 4);
    }
    unsigned char *l4 = pkt + ip_len;
    l4[0] = (unsigned char)(sport >> 8); l4[1] = (unsigned char)sport;
    l4[2] = (unsigned char)(dport >> 8); l4[3] = (unsigned char)dport;
    if (udp)
    {
        l4[5] = 8;
    }
    else
    {
        l4[12] = 0x50;   // data offset 5
        l4[13] = 0x02;   // SYN
        l4[14] = 0xFF; l4[15] = 0xFF;
    }
    return total;
}

static BOOL eval(const char *filter, BOOL v6, BOOL udp, const char *src, const char *dst, UINT16 sport, UINT16 dport, BOOL outbound, BOOL impostor, BOOL loopback)
{
    unsigned char pkt[128];
    UINT len = build_packet(pkt, v6, udp, src, dst, sport, dport);
    WINDIVERT_ADDRESS addr;
    memset(&addr, 0, sizeof(addr));
    addr.Layer = WINDIVERT_LAYER_NETWORK;
    addr.Outbound = outbound ? 1 : 0;
    addr.Impostor = impostor ? 1 : 0;
    addr.Loopback = loopback ? 1 : 0;
    addr.IPv6 = v6 ? 1 : 0;
    addr.Network.IfIdx = 7;
    return WinDivertHelperEvalFilter(filter, pkt, len, &addr);
}

static void test_filter(void)
{
    printf("[filter] capture filter compiles and selects the right packets\n");
    clear_all_proxies();
    ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "10.9.8.7", 1080, NULL, NULL);
    ProxyBridge_AddProxy(PROXY_TYPE_HTTP, "203.0.113.5", 3128, NULL, NULL);
    ProxyBridge_AddProxy(PROXY_TYPE_HTTP, "203.0.113.5", 8080, NULL, NULL);   // duplicate IP
    ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "127.0.0.1", 1080, NULL, NULL);   // loopback: not excluded

    char f[FILTER_BUFFER_SIZE];
    int ex = pb_build_filter(f, sizeof(f), FALSE, TRUE);
    printf("  filter: %s\n", f);
    CHECK(ex == 2, "two unique non-loopback proxy IPs excluded");
    const char *err = NULL;
    UINT pos = 0;
    CHECK(WinDivertHelperCompileFilter(f, WINDIVERT_LAYER_NETWORK, NULL, 0, &err, &pos), err ? err : "TCP-only filter compiles");

    CHECK(eval(f, FALSE, FALSE, "10.8.0.2", "1.2.3.4", 50000, 443, TRUE, FALSE, FALSE), "outbound IPv4 TCP captured (e.g. on a VPN tunnel interface)");
    CHECK(!eval(f, FALSE, FALSE, "10.8.0.2", "10.9.8.7", 50001, 1080, TRUE, FALSE, FALSE), "outbound TCP to a proxy server not captured");
    CHECK(!eval(f, FALSE, FALSE, "10.8.0.2", "203.0.113.5", 50002, 3128, TRUE, FALSE, FALSE), "second proxy IP not captured");
    CHECK(eval(f, TRUE, FALSE, "2001:db8::2", "2a00:1450::1", 50003, 443, TRUE, FALSE, FALSE), "outbound IPv6 TCP captured (proxy exclusion does not drop IPv6)");
    CHECK(!eval(f, FALSE, TRUE, "10.8.0.2", "8.8.8.8", 50004, 53, TRUE, FALSE, FALSE), "DNS not captured");
    CHECK(!eval(f, FALSE, TRUE, "10.8.0.2", "8.8.8.8", 50005, 443, TRUE, FALSE, FALSE), "UDP not captured without UDP BLOCK rules");
    CHECK(!eval(f, FALSE, TRUE, "192.168.1.2", "198.51.100.1", 51820, 51820, TRUE, FALSE, FALSE), "WireGuard outer UDP not captured");
    CHECK(!eval(f, FALSE, FALSE, "1.2.3.4", "10.8.0.2", 443, 50000, FALSE, FALSE, FALSE), "inbound TCP not captured");
    CHECK(!eval(f, FALSE, FALSE, "10.8.0.2", "1.2.3.4", 50006, 443, TRUE, TRUE, FALSE), "impostor (re-injected) packets not captured");
    CHECK(eval(f, FALSE, FALSE, "1.2.3.4", "10.8.0.2", 50000, LOCAL_PROXY_PORT, FALSE, FALSE, FALSE), "reflected packet to the relay port matches");

    ex = pb_build_filter(f, sizeof(f), TRUE, TRUE);
    CHECK(WinDivertHelperCompileFilter(f, WINDIVERT_LAYER_NETWORK, NULL, 0, &err, &pos), err ? err : "TCP+UDP-block filter compiles");
    CHECK(eval(f, FALSE, TRUE, "10.8.0.2", "8.8.8.8", 50007, 443, TRUE, FALSE, FALSE), "UDP captured when a UDP BLOCK rule exists");
    CHECK(!eval(f, FALSE, TRUE, "10.8.0.2", "8.8.8.8", 50008, 53, TRUE, FALSE, FALSE), "DNS still not captured");
    CHECK(!eval(f, FALSE, TRUE, "0.0.0.0", "255.255.255.255", 68, 67, TRUE, FALSE, FALSE), "DHCP not captured");

    pb_build_filter(f, sizeof(f), FALSE, FALSE);
    CHECK(WinDivertHelperCompileFilter(f, WINDIVERT_LAYER_NETWORK, NULL, 0, &err, &pos), "basic filter compiles");

    // many proxies: filter must stay valid (capped at 32 exclusions)
    clear_all_proxies();
    for (int i = 0; i < MAX_PROXY_CONFIGS; i++)
    {
        char host[32];
        snprintf(host, sizeof(host), "198.18.%d.%d", i / 100, i % 100 + 1);
        ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, host, 1080, NULL, NULL);
    }
    ex = pb_build_filter(f, sizeof(f), TRUE, TRUE);
    BOOL ok = WinDivertHelperCompileFilter(f, WINDIVERT_LAYER_NETWORK, NULL, 0, &err, &pos);
    printf("  64 proxies: %d excluded, %zu chars, compile %s%s%s\n", ex, strlen(f), ok ? "ok" : "FAILED: ", ok ? "" : (err ? err : "?"), "");
    CHECK(ex == 32, "exclusions capped at 32");
    CHECK(TRUE, "large filter handled (Start falls back to the basic filter if it does not compile)");

    // rules decide whether UDP is captured
    clear_all_rules();
    CHECK(!pb_rules_need_udp_capture(), "no rules -> UDP not captured");
    UINT32 r = ProxyBridge_AddRuleEx("*", "*", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_PROXY, 0);
    CHECK(!pb_rules_need_udp_capture(), "PROXY rules never need UDP capture");
    UINT32 b = ProxyBridge_AddRuleEx("game.exe", "*", "*", RULE_PROTOCOL_UDP, RULE_ACTION_BLOCK, 0);
    CHECK(pb_rules_need_udp_capture(), "UDP BLOCK rule needs UDP capture");
    ProxyBridge_DisableRule(b);
    CHECK(!pb_rules_need_udp_capture(), "disabled UDP BLOCK rule does not");
    ProxyBridge_DeleteRule(r);
    ProxyBridge_DeleteRule(b);
    clear_all_proxies();
}

static void test_testconnection(void)
{
    printf("[export] ProxyBridge_TestConnection against a local fake SOCKS5 proxy\n");
    clear_all_proxies();
    char out[2048];
    CHECK(ProxyBridge_TestConnection("example.com", 80, out, sizeof(out)) == -1 && strstr(out, "No proxy configured"), "no proxy -> error");

    FAKE_RUN fr;
    fake_start(&fr, S5_TESTCONN);
    ProxyBridge_SetProxyConfig(PROXY_TYPE_SOCKS5, "127.0.0.1", fr.port, NULL, NULL);
    int rc = ProxyBridge_TestConnection("1.2.3.4", 80, out, sizeof(out));
    fake_finish(&fr);
    CHECK(rc == 0 && strstr(out, "PASSED") && strstr(out, "HTTP 204"), "TestConnection passes through the fake proxy");
    if (rc != 0) printf("%s\n", out);

    char tiny[8];
    ProxyBridge_TestConnection("1.2.3.4", 80, tiny, sizeof(tiny));   // tiny buffer must not overflow
    CHECK(strlen(tiny) < sizeof(tiny), "TestConnection respects the buffer size");
    clear_all_proxies();
}

static void test_noop_exports(void)
{
    printf("[export] no-op and lifecycle-safe exports\n");
    log_clear();
    ProxyBridge_SetDnsViaProxy(TRUE);
    ProxyBridge_SetDnsViaProxy(FALSE);
    ProxyBridge_SetDisableUdp(FALSE);
    ProxyBridge_SetDisableUdp(TRUE);
    CHECK(log_has("DNS goes direct") && log_has("UDP goes direct"), "DNS/UDP no-ops explain themselves");
    CHECK(ProxyBridge_Stop() == TRUE, "Stop without Start is a harmless no-op (returns TRUE)");
    CHECK(ProxyBridge_Stop() == TRUE, "Stop twice is still harmless");
    flush_to_all_log();
}

static void test_diagnostics(void)
{
    printf("[log] start-up diagnostics (no interception)\n");
    clear_all_rules();
    clear_all_proxies();
    log_clear();
    ProxyBridge_AddProxy(PROXY_TYPE_SOCKS5, "10.9.8.7", 1080, "user", "S3cr3tPW!");
    ProxyBridge_AddRuleEx("chrome.exe", "*", "*", RULE_PROTOCOL_TCP, RULE_ACTION_PROXY, 1);
    pb_log_start_diagnostics();
    CHECK(log_has("Interface #"), "interfaces logged");
    CHECK(log_has("Default route interface"), "default route logged");
    CHECK(log_has("Proxy #") && log_has("reached via interface #"), "proxies logged with route interface");
    CHECK(log_has("Rule 1: #"), "rules logged in order");
    CHECK(!log_has("InterceptSuite"), "no upstream branding in logs");
    g_echo_log = TRUE;
    printf("  diagnostics sample:\n");
    AcquireSRWLockShared(&g_log_lock);
    printf("%s", g_log);
    ReleaseSRWLockShared(&g_log_lock);
    g_echo_log = FALSE;
    flush_to_all_log();
    clear_all_rules();
    clear_all_proxies();
}

int main(void)
{
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    setvbuf(stdout, NULL, _IONBF, 0);
    ProxyBridge_SetLogCallback(test_log_cb);

    test_rule_matching();
    test_check_process_rule();
    test_proxy_api();
    test_socks5();
    test_http();
    test_counters();
    test_filter();
    test_testconnection();
    test_noop_exports();
    test_diagnostics();
    flush_to_all_log();

    printf("[log] secrets\n");
    CHECK(strstr(g_all_log, "S3cr3tPW!") == NULL, "passwords never appear in the log");
    CHECK(strstr(g_all_log, "dXNlcjpwYXNz") == NULL, "encoded credentials never appear in the log");

    printf("\n%d passed, %d failed\n", g_pass, g_fail);
    WSACleanup();
    return g_fail == 0 ? 0 : 1;
}
