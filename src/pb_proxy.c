/*
 * ProxyBridgeCore proxy configuration store.
 *
 * Ported from the ProxyBridge Windows core (v4.x),
 * Copyright (c) 2025 Anof-cyber/InterceptSuite, MIT License.
 * Modifications copyright (c) 2026 ProxyBridge Team, MIT License:
 *   exact id lookup (unknown ids no longer fall back to the first proxy),
 *   id 0 means the default proxy (SetProxyConfig), store guarded by g_proxy_lock,
 *   snapshot helper for relay threads, proxy-server IP check, management API made
 *   internal (pb_proxy_*), proxy test exports removed (see ProxyBridge_TestConnection).
 */
#include "pb_internal.h"

// Caller must hold g_proxy_lock (shared or exclusive).
static PROXY_CONFIG* find_proxy_config_locked(UINT32 config_id)
{
    if (config_id == 0)
    {
        // "Default proxy": the legacy single proxy when it is set, else the first one.
        if (g_legacy_proxy_id != 0)
        {
            for (int i = 0; i < g_proxy_config_count; i++)
                if (g_proxy_configs[i].config_id == g_legacy_proxy_id)
                    return &g_proxy_configs[i];
        }
        return (g_proxy_config_count > 0) ? &g_proxy_configs[0] : NULL;
    }
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        if (g_proxy_configs[i].config_id == config_id)
            return &g_proxy_configs[i];
    }
    return NULL;
}

// Lock-free lookup kept for the logging paths in the packet loop (the array is static,
// so a concurrent edit can at worst produce a stale string, never a dangling pointer).
// Code that needs a stable copy (relay threads) uses pb_proxy_snapshot().
PROXY_CONFIG* find_proxy_config(UINT32 config_id)
{
    AcquireSRWLockShared(&g_proxy_lock);
    PROXY_CONFIG *cfg = find_proxy_config_locked(config_id);
    ReleaseSRWLockShared(&g_proxy_lock);
    return cfg;
}

BOOL pb_proxy_snapshot(UINT32 config_id, PROXY_CONFIG *out)
{
    BOOL ok = FALSE;
    AcquireSRWLockShared(&g_proxy_lock);
    PROXY_CONFIG *cfg = find_proxy_config_locked(config_id);
    if (cfg != NULL)
    {
        memcpy(out, cfg, sizeof(*out));
        ok = TRUE;
    }
    ReleaseSRWLockShared(&g_proxy_lock);
    return ok;
}

BOOL pb_is_proxy_server_ip(UINT32 ip)
{
    if (ip == 0)
        return FALSE;
    BOOL hit = FALSE;
    AcquireSRWLockShared(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        if (g_proxy_configs[i].resolved_ip == ip)
        {
            hit = TRUE;
            break;
        }
    }
    ReleaseSRWLockShared(&g_proxy_lock);
    return hit;
}

// Helper: check if any proxy config is SOCKS5 (needed to decide whether to start UDP relay)
BOOL any_socks5_config(void)
{
    BOOL any = FALSE;
    AcquireSRWLockShared(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        if (g_proxy_configs[i].type == PROXY_TYPE_SOCKS5 &&
            g_proxy_configs[i].host[0] != '\0' &&
            g_proxy_configs[i].port != 0)
        {
            any = TRUE;
            break;
        }
    }
    ReleaseSRWLockShared(&g_proxy_lock);
    return any;
}

// TRUE if any enabled PROXY rule routes traffic through this proxy config. A rule with
// proxy_config_id 0 means "default proxy", which could resolve to any config, so its
// presence marks all configs as potentially used.
BOOL is_proxy_config_referenced(UINT32 config_id)
{
    BOOL referenced = FALSE;
    AcquireSRWLockShared(&g_rules_lock);
    for (PROCESS_RULE *r = rules_list; r != NULL; r = r->next)
    {
        if (!r->enabled || r->action != RULE_ACTION_PROXY)
            continue;
        if (r->proxy_config_id == config_id || r->proxy_config_id == 0)
        {
            referenced = TRUE;
            break;
        }
    }
    ReleaseSRWLockShared(&g_rules_lock);
    return referenced;
}

static void close_udp_state(PROXY_CONFIG *cfg)
{
    if (cfg->udp_tcp_ctrl != INVALID_SOCKET)  { closesocket(cfg->udp_tcp_ctrl);  cfg->udp_tcp_ctrl  = INVALID_SOCKET; }
    if (cfg->udp_send_sock != INVALID_SOCKET) { closesocket(cfg->udp_send_sock); cfg->udp_send_sock = INVALID_SOCKET; }
    cfg->udp_connected = FALSE;
}

// Returns the new config id (> 0) or 0. The host is resolved once here (IPv4).
UINT32 pb_proxy_add(ProxyType type, const char* proxy_host, UINT16 proxy_port, const char* username, const char* password, BOOL send_domain_to_proxy)
{
    if (proxy_host == NULL || proxy_host[0] == '\0' || proxy_port == 0)
        return 0;

    // Resolve outside the lock (DNS can be slow).
    UINT32 resolved = resolve_hostname(proxy_host);
    if (resolved == 0)
        return 0;

    UINT32 id = 0;
    AcquireSRWLockExclusive(&g_proxy_lock);
    if (g_proxy_config_count < MAX_PROXY_CONFIGS)
    {
        PROXY_CONFIG *cfg = &g_proxy_configs[g_proxy_config_count];
        memset(cfg, 0, sizeof(PROXY_CONFIG));

        cfg->config_id = g_next_config_id++;
        cfg->type      = (type == PROXY_TYPE_HTTP) ? PROXY_TYPE_HTTP : PROXY_TYPE_SOCKS5;
        cfg->port      = proxy_port;
        cfg->send_domain_to_proxy = send_domain_to_proxy;
        strncpy_s(cfg->host, sizeof(cfg->host), proxy_host, _TRUNCATE);
        cfg->resolved_ip = resolved;
        if (username != NULL) strncpy_s(cfg->username, sizeof(cfg->username), username, _TRUNCATE);
        if (password != NULL) strncpy_s(cfg->password, sizeof(cfg->password), password, _TRUNCATE);
        cfg->udp_tcp_ctrl  = INVALID_SOCKET;
        cfg->udp_send_sock = INVALID_SOCKET;
        cfg->udp_connected = FALSE;

        g_proxy_config_count++;
        id = cfg->config_id;
    }
    ReleaseSRWLockExclusive(&g_proxy_lock);

    if (id == 0)
        log_message("Proxy table full (%d entries): %s:%u not added", MAX_PROXY_CONFIGS, proxy_host, proxy_port);
    return id;
}

BOOL pb_proxy_edit(UINT32 config_id, ProxyType type, const char* proxy_host, UINT16 proxy_port, const char* username, const char* password, BOOL send_domain_to_proxy)
{
    if (proxy_host == NULL || proxy_host[0] == '\0' || proxy_port == 0 || config_id == 0)
        return FALSE;

    UINT32 resolved = resolve_hostname(proxy_host);
    if (resolved == 0)
        return FALSE;

    BOOL found = FALSE;
    AcquireSRWLockExclusive(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        PROXY_CONFIG *cfg = &g_proxy_configs[i];
        if (cfg->config_id == config_id)
        {
            close_udp_state(cfg);
            cfg->type = (type == PROXY_TYPE_HTTP) ? PROXY_TYPE_HTTP : PROXY_TYPE_SOCKS5;
            cfg->port = proxy_port;
            cfg->send_domain_to_proxy = send_domain_to_proxy;
            strncpy_s(cfg->host, sizeof(cfg->host), proxy_host, _TRUNCATE);
            cfg->resolved_ip = resolved;
            cfg->username[0] = '\0';
            cfg->password[0] = '\0';
            if (username != NULL) strncpy_s(cfg->username, sizeof(cfg->username), username, _TRUNCATE);
            if (password != NULL) strncpy_s(cfg->password, sizeof(cfg->password), password, _TRUNCATE);
            found = TRUE;
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_proxy_lock);
    return found;
}

BOOL pb_proxy_delete(UINT32 config_id)
{
    BOOL found = FALSE;
    AcquireSRWLockExclusive(&g_proxy_lock);
    for (int i = 0; i < g_proxy_config_count; i++)
    {
        PROXY_CONFIG *cfg = &g_proxy_configs[i];
        if (cfg->config_id == config_id)
        {
            close_udp_state(cfg);
            SecureZeroMemory(cfg->password, sizeof(cfg->password));

            // Shift remaining entries down
            int remaining = g_proxy_config_count - i - 1;
            if (remaining > 0)
                memmove(&g_proxy_configs[i], &g_proxy_configs[i + 1], remaining * sizeof(PROXY_CONFIG));

            g_proxy_config_count--;
            SecureZeroMemory(&g_proxy_configs[g_proxy_config_count], sizeof(PROXY_CONFIG));
            if (g_legacy_proxy_id == config_id)
                g_legacy_proxy_id = 0;
            found = TRUE;
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_proxy_lock);
    return found;
}
