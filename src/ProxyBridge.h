/*
 * ProxyBridgeCore.dll public API. This header is the binding contract with the GUI and
 * the CLI; see docs/CORE_API.md for the semantics of every export and docs/CORE_NOTES.md
 * for how the core works. All exports use the cdecl calling convention.
 */
#ifndef PROXYBRIDGE_H
#define PROXYBRIDGE_H

#include <windows.h>

#ifdef PROXYBRIDGE_EXPORTS
#define PROXYBRIDGE_API __declspec(dllexport)
#else
#define PROXYBRIDGE_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*LogCallback)(const char* message);
typedef void (*ConnectionCallback)(const char* process_name, DWORD pid, const char* dest_ip, UINT16 dest_port, const char* proxy_info);

typedef enum {
    PROXY_TYPE_HTTP = 0,
    PROXY_TYPE_SOCKS5 = 1
} ProxyType;

typedef enum {
    RULE_ACTION_PROXY = 0,
    RULE_ACTION_DIRECT = 1,
    RULE_ACTION_BLOCK = 2
} RuleAction;

typedef enum {
    RULE_PROTOCOL_TCP = 0,
    RULE_PROTOCOL_UDP = 1,
    RULE_PROTOCOL_BOTH = 2
} RuleProtocol;

PROXYBRIDGE_API UINT32 ProxyBridge_AddRule(const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action);
// Registers a proxy (host may be an IP or a hostname, resolved once here, IPv4).
// Returns its proxy id (> 0) or 0 on failure. Rules reference it through proxy_id.
PROXYBRIDGE_API UINT32 ProxyBridge_AddProxy(ProxyType type, const char* proxy_ip, UINT16 proxy_port, const char* username, const char* password);
PROXYBRIDGE_API void ProxyBridge_ClearProxies(void);
// Rules are evaluated in insertion order, first match wins, no match means DIRECT.
// process_name: exe name (case-insensitive), full path, wildcard ("fire*.exe") or "*".
// proxy_id is used for RULE_ACTION_PROXY; 0 means the default proxy (SetProxyConfig).
PROXYBRIDGE_API UINT32 ProxyBridge_AddRuleEx(const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action, UINT32 proxy_id);
PROXYBRIDGE_API BOOL ProxyBridge_EnableRule(UINT32 rule_id);
PROXYBRIDGE_API BOOL ProxyBridge_DisableRule(UINT32 rule_id);
PROXYBRIDGE_API BOOL ProxyBridge_DeleteRule(UINT32 rule_id);
PROXYBRIDGE_API BOOL ProxyBridge_EditRule(UINT32 rule_id, const char* process_name, const char* target_hosts, const char* target_ports, RuleProtocol protocol, RuleAction action);
PROXYBRIDGE_API BOOL ProxyBridge_SetProxyConfig(ProxyType type, const char* proxy_ip, UINT16 proxy_port, const char* username, const char* password);  // proxy_ip can be IP address or hostname
PROXYBRIDGE_API void ProxyBridge_SetDnsViaProxy(BOOL enable);   // no-op: DNS always goes direct
PROXYBRIDGE_API void ProxyBridge_SetDisableUdp(BOOL disable);    // no-op: UDP is never proxied
PROXYBRIDGE_API void ProxyBridge_SetLogCallback(LogCallback callback);
PROXYBRIDGE_API void ProxyBridge_SetConnectionCallback(ConnectionCallback callback);
PROXYBRIDGE_API BOOL ProxyBridge_Start(void);   // resets the traffic counters
PROXYBRIDGE_API BOOL ProxyBridge_Stop(void);    // idempotent: returns TRUE once everything is released
PROXYBRIDGE_API void ProxyBridge_GetTrafficStats(UINT64* bytes_up, UINT64* bytes_down);  // cumulative since Start
PROXYBRIDGE_API int ProxyBridge_TestConnection(const char* target_host, UINT16 target_port, char* result_buffer, size_t buffer_size);

#ifdef __cplusplus
}
#endif

#endif
