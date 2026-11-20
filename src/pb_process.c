/*
 * ProxyBridgeCore process resolution: source port to PID lookups and the PID cache.
 *
 * Ported from the ProxyBridge Windows core (v4.x),
 * Copyright (c) 2025 Anof-cyber/InterceptSuite, MIT License.
 * Modifications copyright (c) 2026 ProxyBridge Team, MIT License.
 */
#include "pb_internal.h"

// Reusable grow-only scratch buffer for the TCP/UDP owner-PID tables. These lookups run
// only on the single packet-processor thread (NUM_PACKET_THREADS == 1) and each call
// finishes scanning before the next one, so one shared buffer is safe and lets us skip a
// malloc/free of the (potentially tens-of-KB) table on every new connection. Freed in Stop.

void *pidtbl_reserve(DWORD need)
{
    if (need > g_pidtbl_cap)
    {
        DWORD newcap = g_pidtbl_cap ? g_pidtbl_cap : 16384;
        while (newcap < need)
        {
            if (newcap > (0xFFFFFFFFu / 2)) { newcap = need; break; }
            newcap *= 2;
        }
        char *nb = (char *)realloc(g_pidtbl_buf, newcap);
        if (nb == NULL) return NULL;   // keep the old buffer intact on failure
        g_pidtbl_buf = nb;
        g_pidtbl_cap = newcap;
    }
    return g_pidtbl_buf;
}

DWORD get_process_id_from_connection(UINT32 src_ip, UINT16 src_port)
{
    // check cache first
    DWORD cached_pid = get_cached_pid(src_ip, src_port, FALSE);
    if (cached_pid != 0)
        return cached_pid;

    DWORD size = 0;
    DWORD pid = 0;

    if (GetExtendedTcpTable(NULL, &size, FALSE, AF_INET,
                            TCP_TABLE_OWNER_PID_ALL, 0) != ERROR_INSUFFICIENT_BUFFER)
    {
        return 0;
    }

    MIB_TCPTABLE_OWNER_PID *tcp_table = (MIB_TCPTABLE_OWNER_PID *)pidtbl_reserve(size);
    if (tcp_table == NULL)
    {
        return 0;
    }

    if (GetExtendedTcpTable(tcp_table, &size, FALSE, AF_INET,
                            TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR)
    {
        return 0;   // buffer is reused, not freed
    }

    for (DWORD i = 0; i < tcp_table->dwNumEntries; i++)
    {
        MIB_TCPROW_OWNER_PID *row = &tcp_table->table[i];

        if (row->dwLocalAddr == src_ip &&
            ntohs((UINT16)row->dwLocalPort) == src_port)
        {
            pid = row->dwOwningPid;
            break;
        }
    }

    // store cache the result
    if (pid != 0)
        cache_pid(src_ip, src_port, pid, FALSE);

    return pid;
}

// Get process ID for UDP connection
DWORD get_process_id_from_udp_connection(UINT32 src_ip, UINT16 src_port)
{
    DWORD cached_pid = get_cached_pid(src_ip, src_port, TRUE);
    if (cached_pid != 0)
        return cached_pid;

    DWORD size = 0;
    DWORD pid = 0;

    if (GetExtendedUdpTable(NULL, &size, FALSE, AF_INET,
                            UDP_TABLE_OWNER_PID, 0) != ERROR_INSUFFICIENT_BUFFER)
    {
        return 0;
    }

    MIB_UDPTABLE_OWNER_PID *udp_table = (MIB_UDPTABLE_OWNER_PID *)pidtbl_reserve(size);
    if (udp_table == NULL)
    {
        return 0;
    }

    if (GetExtendedUdpTable(udp_table, &size, FALSE, AF_INET,
                            UDP_TABLE_OWNER_PID, 0) != NO_ERROR)
    {
        return 0;   // buffer is reused, not freed
    }

    // First pass: Try exact match (IP + port)
    for (DWORD i = 0; i < udp_table->dwNumEntries; i++)
    {
        MIB_UDPROW_OWNER_PID *row = &udp_table->table[i];

        if (row->dwLocalAddr == src_ip &&
            ntohs((UINT16)row->dwLocalPort) == src_port)
        {
            pid = row->dwOwningPid;
            break;
        }
    }

    // Second pass: If not found, try matching port on 0.0.0.0 (INADDR_ANY)
    // Many UDP applications bind to 0.0.0.0:port instead of specific IP
    if (pid == 0)
    {
        for (DWORD i = 0; i < udp_table->dwNumEntries; i++)
        {
            MIB_UDPROW_OWNER_PID *row = &udp_table->table[i];

            if (row->dwLocalAddr == 0 &&  // 0.0.0.0 (INADDR_ANY)
                ntohs((UINT16)row->dwLocalPort) == src_port)
            {
                pid = row->dwOwningPid;
                break;
            }
        }
    }

    if (pid != 0)
        cache_pid(src_ip, src_port, pid, TRUE);

    return pid;
}

DWORD get_process_id_from_connection_v6(const UINT8 src_ip6[16], UINT16 src_port)
{
    DWORD size = 0, pid = 0;

    if (GetExtendedTcpTable(NULL, &size, FALSE, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0) != ERROR_INSUFFICIENT_BUFFER)
        return 0;
    MIB_TCP6TABLE_OWNER_PID *tcp_table = (MIB_TCP6TABLE_OWNER_PID *)pidtbl_reserve(size);
    if (!tcp_table) return 0;
    if (GetExtendedTcpTable(tcp_table, &size, FALSE, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0) == NO_ERROR)
    {
        for (DWORD i = 0; i < tcp_table->dwNumEntries; i++)
        {
            MIB_TCP6ROW_OWNER_PID *row = &tcp_table->table[i];
            if (ntohs((UINT16)row->dwLocalPort) == src_port &&
                memcmp(row->ucLocalAddr, src_ip6, 16) == 0)
            {
                pid = row->dwOwningPid;
                break;
            }
        }
    }
    return pid;   // buffer is reused, not freed
}

DWORD get_process_id_from_udp_connection_v6(const UINT8 src_ip6[16], UINT16 src_port)
{
    DWORD size = 0, pid = 0;

    if (GetExtendedUdpTable(NULL, &size, FALSE, AF_INET6, UDP_TABLE_OWNER_PID, 0) != ERROR_INSUFFICIENT_BUFFER)
        return 0;
    MIB_UDP6TABLE_OWNER_PID *udp_table = (MIB_UDP6TABLE_OWNER_PID *)pidtbl_reserve(size);
    if (!udp_table) return 0;
    if (GetExtendedUdpTable(udp_table, &size, FALSE, AF_INET6, UDP_TABLE_OWNER_PID, 0) == NO_ERROR)
    {
        for (DWORD i = 0; i < udp_table->dwNumEntries; i++)
        {
            MIB_UDP6ROW_OWNER_PID *row = &udp_table->table[i];
            if (ntohs((UINT16)row->dwLocalPort) == src_port &&
                (memcmp(row->ucLocalAddr, src_ip6, 16) == 0 ||
                 memcmp(row->ucLocalAddr, "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0", 16) == 0))
            {
                pid = row->dwOwningPid;
                break;
            }
        }
    }
    return pid;   // buffer is reused, not freed
}

BOOL get_process_name_from_pid(DWORD pid, char *name, DWORD name_size)
{
    HANDLE hProcess;
    WCHAR full_path_w[MAX_PATH];
    DWORD path_len = MAX_PATH;

    if (pid == 0)
    {
        return FALSE;
    }

    // ERROR in getting process name for PID 4 reserved by system
    // SMB is managed by system process
    if (pid == 4)
    {
        strncpy_s(name, name_size, "System", _TRUNCATE);
        return TRUE;
    }

    hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (hProcess == NULL)
    {
        return FALSE;
    }

    if (QueryFullProcessImageNameW(hProcess, 0, full_path_w, &path_len))
    {
        // Convert wide string to UTF-8 so Chinese/non-ASCII paths are preserved
        int converted = WideCharToMultiByte(CP_UTF8, 0, full_path_w, -1, name, (int)name_size, NULL, NULL);
        CloseHandle(hProcess);
        return converted > 0;
    }

    CloseHandle(hProcess);
    return FALSE;
}

// ---- Parent process (rule inheritance, see match_rule_inherited in pb_rules.c) ----

typedef LONG (NTAPI *NtQueryInformationProcess_t)(HANDLE, int, PVOID, ULONG, PULONG);
typedef struct {
    PVOID Reserved1;
    PVOID PebBaseAddress;
    PVOID Reserved2[2];
    ULONG_PTR UniqueProcessId;
    ULONG_PTR InheritedFromUniqueProcessId;
} PB_PROCESS_BASIC_INFORMATION;

static BOOL process_start_time(HANDLE h, ULONGLONG *out)
{
    FILETIME c, e, k, u;
    if (!GetProcessTimes(h, &c, &e, &k, &u))
        return FALSE;
    *out = ((ULONGLONG)c.dwHighDateTime << 32) | c.dwLowDateTime;
    return TRUE;
}

// Recent answers, so a program opening many connections does not query the kernel for
// its ancestry every time. Only the packet thread and tests call this.
#define PARENT_CACHE_SLOTS 64
#define PARENT_CACHE_MS 5000
typedef struct { DWORD pid, parent; ULONGLONG when; } PARENT_CACHE_SLOT;
static PARENT_CACHE_SLOT g_parent_cache[PARENT_CACHE_SLOTS];

// The parent process id, or 0 when there is none, it cannot be read, or the parent has
// exited and its id was reused (a process that started after the child is not its parent).
// ntdll is resolved at run time so the DLL gains no import (see build-installer.ps1).
DWORD get_parent_process_id(DWORD pid)
{
    static NtQueryInformationProcess_t query = NULL;
    static BOOL looked_up = FALSE;

    if (pid <= 4)
        return 0;
    ULONGLONG now = GetTickCount64();
    PARENT_CACHE_SLOT *slot = &g_parent_cache[pid % PARENT_CACHE_SLOTS];
    if (slot->pid == pid && now - slot->when < PARENT_CACHE_MS)
        return slot->parent;

    if (!looked_up)
    {
        HMODULE nt = GetModuleHandleW(L"ntdll.dll");
        if (nt != NULL)
            query = (NtQueryInformationProcess_t)(void *)GetProcAddress(nt, "NtQueryInformationProcess");
        looked_up = TRUE;
    }
    DWORD parent = 0;
    if (query != NULL)
    {
        HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (h != NULL)
        {
            PB_PROCESS_BASIC_INFORMATION pbi;
            ULONG len = 0;
            ULONGLONG child_start = 0;
            if (query(h, 0 /* ProcessBasicInformation */, &pbi, sizeof(pbi), &len) == 0 && process_start_time(h, &child_start))
                parent = (DWORD)pbi.InheritedFromUniqueProcessId;
            CloseHandle(h);
            if (parent == pid)
                parent = 0;
            if (parent != 0)
            {
                HANDLE hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, parent);
                ULONGLONG parent_start = 0;
                if (hp == NULL || !process_start_time(hp, &parent_start) || parent_start > child_start)
                    parent = 0;
                if (hp != NULL)
                    CloseHandle(hp);
            }
        }
    }
    slot->pid = pid;
    slot->parent = parent;
    slot->when = now;
    return parent;
}

void clear_parent_cache(void)
{
    memset(g_parent_cache, 0, sizeof(g_parent_cache));
}

//  cache pid
// This can be imprroved
// Need to work on this before releease for potential collusion
// need to remove unwanted entires from table
UINT32 pid_cache_hash(UINT32 src_ip, UINT16 src_port, BOOL is_udp)
{
    UINT32 hash = src_ip ^ ((UINT32)src_port << 16) ^ (is_udp ? 0x80000000 : 0);
    return hash % PID_CACHE_SIZE;
}

DWORD get_cached_pid(UINT32 src_ip, UINT16 src_port, BOOL is_udp)
{
    UINT32 hash = pid_cache_hash(src_ip, src_port, is_udp);
    ULONGLONG current_time = GetTickCount64();
    DWORD pid = 0;

    AcquireSRWLockShared(&lock);

    PID_CACHE_ENTRY *entry = pid_cache[hash];
    while (entry != NULL)
    {
        if (entry->src_ip == src_ip &&
            entry->src_port == src_port &&
            entry->is_udp == is_udp)
        {
            pid = (current_time - entry->timestamp < PID_CACHE_TTL_MS) ? entry->pid : 0;
            break;
        }
        entry = entry->next;
    }

    ReleaseSRWLockShared(&lock);
    return pid;
}

void cache_pid(UINT32 src_ip, UINT16 src_port, DWORD pid, BOOL is_udp)
{
    UINT32 hash = pid_cache_hash(src_ip, src_port, is_udp);
    ULONGLONG current_time = GetTickCount64();

    AcquireSRWLockExclusive(&lock);

    PID_CACHE_ENTRY *entry = pid_cache[hash];
    while (entry != NULL)
    {
        if (entry->src_ip == src_ip &&
            entry->src_port == src_port &&
            entry->is_udp == is_udp)
        {
            entry->pid = pid;
            entry->timestamp = current_time;
            ReleaseSRWLockExclusive(&lock);
            return;
        }
        entry = entry->next;
    }

    PID_CACHE_ENTRY *new_entry = (PID_CACHE_ENTRY *)malloc(sizeof(PID_CACHE_ENTRY));
    if (new_entry != NULL)
    {
        new_entry->src_ip = src_ip;
        new_entry->src_port = src_port;
        new_entry->pid = pid;
        new_entry->timestamp = current_time;
        new_entry->is_udp = is_udp;
        new_entry->next = pid_cache[hash];
        pid_cache[hash] = new_entry;
    }

    ReleaseSRWLockExclusive(&lock);
}

void clear_pid_cache(void)
{
    AcquireSRWLockExclusive(&lock);

    for (int i = 0; i < PID_CACHE_SIZE; i++)
    {
        while (pid_cache[i] != NULL)
        {
            PID_CACHE_ENTRY *to_free = pid_cache[i];
            pid_cache[i] = pid_cache[i]->next;
            free(to_free);
        }
    }

    ReleaseSRWLockExclusive(&lock);
}

// Evict the cached PID for a specific (src_ip, src_port). Called when a new TCP
// connection begins (fresh SYN) on a port: Windows reuses ephemeral ports, so any
// PID cached for that port may belong to the previous, now-closed process. Without
// this, a reused port could be matched against the wrong application's rule for up to
// PID_CACHE_TTL_MS. Forcing a re-lookup here keeps the decision correct.
void remove_cached_pid(UINT32 src_ip, UINT16 src_port, BOOL is_udp)
{
    UINT32 hash = pid_cache_hash(src_ip, src_port, is_udp);

    AcquireSRWLockExclusive(&lock);
    PID_CACHE_ENTRY **pp = &pid_cache[hash];
    while (*pp != NULL)
    {
        if ((*pp)->src_ip == src_ip && (*pp)->src_port == src_port && (*pp)->is_udp == is_udp)
        {
            PID_CACHE_ENTRY *to_free = *pp;
            *pp = (*pp)->next;
            free(to_free);
            break;
        }
        pp = &(*pp)->next;
    }
    ReleaseSRWLockExclusive(&lock);
}

// Prune expired PID-cache entries so the table doesn't grow without bound over a long
// session (entries were previously only ignored on lookup, never freed).
void cleanup_stale_pid_cache(void)
{
    ULONGLONG now = GetTickCount64();

    AcquireSRWLockExclusive(&lock);
    for (int i = 0; i < PID_CACHE_SIZE; i++)
    {
        PID_CACHE_ENTRY **pp = &pid_cache[i];
        while (*pp != NULL)
        {
            if (now - (*pp)->timestamp >= PID_CACHE_TTL_MS)
            {
                PID_CACHE_ENTRY *to_free = *pp;
                *pp = (*pp)->next;
                free(to_free);
            }
            else
            {
                pp = &(*pp)->next;
            }
        }
    }
    ReleaseSRWLockExclusive(&lock);
}

