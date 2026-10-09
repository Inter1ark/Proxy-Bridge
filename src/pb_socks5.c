/*
 * ProxyBridgeCore SOCKS5: CONNECT (IPv4/IPv6/domain) and UDP ASSOCIATE.
 *
 * Ported from the ProxyBridge Windows core (v4.x),
 * Copyright (c) 2025 Anof-cyber/InterceptSuite, MIT License.
 * Modifications copyright (c) 2026 ProxyBridge Team, MIT License:
 *   one shared method/auth negotiation (RFC 1928/1929) for every command, reply codes and
 *   WSA errors are logged by name, credential buffers are wiped after use.
 */
#include "pb_internal.h"

static const char *socks5_reply_name(int rep)
{
    switch (rep)
    {
        case 0x00: return "succeeded";
        case 0x01: return "general SOCKS server failure";
        case 0x02: return "connection not allowed by ruleset";
        case 0x03: return "network unreachable";
        case 0x04: return "host unreachable";
        case 0x05: return "connection refused";
        case 0x06: return "TTL expired";
        case 0x07: return "command not supported";
        case 0x08: return "address type not supported";
        case -1:   return "no reply (connection closed or timed out)";
        default:   return "unknown reply";
    }
}

// Method selection + optional username/password sub-negotiation.
// Returns 0 on success, -1 on failure (already logged).
static int socks5_negotiate(SOCKET s, const PROXY_CONFIG *cfg)
{
    unsigned char buf[SOCKS5_BUFFER_SIZE];
    BOOL use_auth = (cfg != NULL && cfg->username[0] != '\0');

    buf[0] = SOCKS5_VERSION;
    int greet_len;
    if (use_auth)
    {
        buf[1] = 0x02;  // Number of methods
        buf[2] = SOCKS5_AUTH_NONE;
        buf[3] = 0x02;  // Username/password auth
        greet_len = 4;
    }
    else
    {
        buf[1] = 0x01;
        buf[2] = SOCKS5_AUTH_NONE;
        greet_len = 3;
    }
    if (send_all(s, (char*)buf, greet_len) == SOCKET_ERROR)
    {
        log_message("SOCKS5: Failed to send greeting (WSA %d)", WSAGetLastError());
        return -1;
    }

    if (recv_n(s, (char*)buf, 2) != 2)
    {
        log_message("SOCKS5: No method selection reply (WSA %d)", WSAGetLastError());
        return -1;
    }
    if (buf[0] != SOCKS5_VERSION)
    {
        log_message("SOCKS5: Server is not SOCKS5 (version byte 0x%02X)", buf[0]);
        return -1;
    }

    if (buf[1] == 0x02)  // Username/password required
    {
        if (!use_auth)
        {
            log_message("SOCKS5: Server requires authentication but no credentials are configured");
            return -1;
        }

        // RFC 1929. Lengths are bounded so the packet always fits in buf.
        size_t user_len = strnlen_s(cfg->username, sizeof(cfg->username));
        size_t pass_len = strnlen_s(cfg->password, sizeof(cfg->password));
        if (user_len == 0 || user_len > 255 || pass_len > 255)
        {
            log_message("SOCKS5: Username or password too long (max 255 bytes each)");
            return -1;
        }

        buf[0] = 0x01;
        buf[1] = (unsigned char)user_len;
        memcpy(&buf[2], cfg->username, user_len);
        buf[2 + user_len] = (unsigned char)pass_len;
        memcpy(&buf[3 + user_len], cfg->password, pass_len);

        int auth_len = (int)(3 + user_len + pass_len);
        int sent = send_all(s, (char*)buf, auth_len);
        SecureZeroMemory(buf, sizeof(buf));
        if (sent == SOCKET_ERROR)
        {
            log_message("SOCKS5: Failed to send credentials (WSA %d)", WSAGetLastError());
            return -1;
        }

        if (recv_n(s, (char*)buf, 2) != 2)
        {
            log_message("SOCKS5: No authentication reply (WSA %d)", WSAGetLastError());
            return -1;
        }
        if (buf[0] != 0x01 || buf[1] != 0x00)
        {
            log_message("SOCKS5: Authentication rejected by proxy (status 0x%02X)", buf[1]);
            return -1;
        }
    }
    else if (buf[1] == 0xFF)
    {
        log_message("SOCKS5: Proxy accepted none of the offered auth methods%s",
            use_auth ? "" : " (it probably requires a username and password)");
        return -1;
    }
    else if (buf[1] != SOCKS5_AUTH_NONE)
    {
        log_message("SOCKS5: Unsupported auth method: 0x%02X", buf[1]);
        return -1;
    }
    return 0;
}

// Read and validate a SOCKS5 CONNECT reply according to RFC 1928.
// The proxy picks the BND.ADDR type independently of the request's ATYP (a few proxies
// answer an IPv6 CONNECT with a 4-byte IPv4 0.0.0.0 BND.ADDR), so parse the 4-byte header
// (VER REP RSV ATYP) and then drain the variable-length BND.ADDR + BND.PORT by ATYP.
int socks5_read_connect_reply(SOCKET s, int *reply)
{
    unsigned char hdr[4];
    int len = recv_n(s, (char*)hdr, 4);
    if (reply) *reply = (len == 4) ? hdr[1] : -1;
    if (len != 4 || hdr[0] != SOCKS5_VERSION || hdr[1] != 0x00) return -1;

    int drain;
    if      (hdr[3] == SOCKS5_ATYP_IPV4) drain = 4 + 2;
    else if (hdr[3] == SOCKS5_ATYP_IPV6) drain = 16 + 2;
    else if (hdr[3] == SOCKS5_ATYP_DOMAIN)
    {
        unsigned char dlen;
        if (recv_n(s, (char*)&dlen, 1) != 1) return -1;
        drain = (int)dlen + 2;
    }
    else return -1;   // unknown ATYP

    unsigned char scratch[270];   // max drain = 255 + 2 (domain) < 270
    if (drain > 0 && recv_n(s, (char*)scratch, drain) != drain) return -1;
    return 0;
}

static int socks5_send_connect_and_check(SOCKET s, const unsigned char *req, int req_len, const char *what)
{
    if (send_all(s, (const char*)req, req_len) == SOCKET_ERROR)
    {
        log_message("SOCKS5: Failed to send CONNECT for %s (WSA %d)", what, WSAGetLastError());
        return -1;
    }

    int reply = -1;
    if (socks5_read_connect_reply(s, &reply) != 0)
    {
        log_message("SOCKS5: CONNECT to %s failed, reply code %d (%s)", what, reply, socks5_reply_name(reply));
        return -1;
    }
    return 0;
}

// SOCKS5 CONNECT with ATYP_DOMAIN
int socks5_connect_domain(SOCKET s, const char *hostname, UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    unsigned char buf[SOCKS5_BUFFER_SIZE];

    size_t hlen = (hostname != NULL) ? strnlen_s(hostname, 256) : 0;
    if (hlen == 0 || hlen > 255) return -1;

    if (socks5_negotiate(s, cfg) != 0) return -1;

    buf[0] = SOCKS5_VERSION;
    buf[1] = SOCKS5_CMD_CONNECT;
    buf[2] = 0x00;
    buf[3] = SOCKS5_ATYP_DOMAIN;
    buf[4] = (unsigned char)hlen;
    memcpy(&buf[5], hostname, hlen);
    buf[5 + hlen] = (dest_port >> 8) & 0xFF;
    buf[6 + hlen] = (dest_port >> 0) & 0xFF;

    char what[300];
    snprintf(what, sizeof(what), "%s:%u", hostname, dest_port);
    return socks5_send_connect_and_check(s, buf, (int)(7 + hlen), what);
}

int socks5_connect(SOCKET s, UINT32 dest_ip, UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    unsigned char buf[10];

    if (socks5_negotiate(s, cfg) != 0) return -1;

    buf[0] = SOCKS5_VERSION;
    buf[1] = SOCKS5_CMD_CONNECT;
    buf[2] = 0x00;
    buf[3] = SOCKS5_ATYP_IPV4;
    buf[4] = (dest_ip >> 0) & 0xFF;
    buf[5] = (dest_ip >> 8) & 0xFF;
    buf[6] = (dest_ip >> 16) & 0xFF;
    buf[7] = (dest_ip >> 24) & 0xFF;
    buf[8] = (dest_port >> 8) & 0xFF;
    buf[9] = (dest_port >> 0) & 0xFF;

    char ip[32], what[64];
    format_ip_address(dest_ip, ip, sizeof(ip));
    snprintf(what, sizeof(what), "%s:%u", ip, dest_port);
    return socks5_send_connect_and_check(s, buf, 10, what);
}

int socks5_connect_v6(SOCKET s, const UINT8 dest_ip6[16], UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    unsigned char buf[22];

    if (socks5_negotiate(s, cfg) != 0) return -1;

    buf[0] = SOCKS5_VERSION;
    buf[1] = SOCKS5_CMD_CONNECT;
    buf[2] = 0x00;
    buf[3] = SOCKS5_ATYP_IPV6;
    memcpy(&buf[4], dest_ip6, 16);
    buf[20] = (dest_port >> 8) & 0xFF;
    buf[21] = (dest_port >> 0) & 0xFF;

    char ip[64], what[80];
    inet_ntop(AF_INET6, (void *)dest_ip6, ip, sizeof(ip));
    snprintf(what, sizeof(what), "[%s]:%u", ip, dest_port);
    return socks5_send_connect_and_check(s, buf, 22, what);
}

int socks5_udp_associate_with_config(SOCKET s, struct sockaddr_in *relay_addr, const PROXY_CONFIG *cfg)
{
    unsigned char buf[10];

    if (socks5_negotiate(s, cfg) != 0) return -1;

    buf[0] = SOCKS5_VERSION;
    buf[1] = SOCKS5_CMD_UDP_ASSOCIATE;
    buf[2] = 0x00;
    buf[3] = SOCKS5_ATYP_IPV4;
    memset(&buf[4], 0, 6);

    if (send_all(s, (char*)buf, 10) == SOCKET_ERROR)
        return -1;

    // Reply: VER REP RSV ATYP BND.ADDR BND.PORT. We relay UDP over IPv4, so an IPv4
    // bound endpoint is required (0.0.0.0 is handled by the caller).
    unsigned char rep[4];
    if (recv_n(s, (char*)rep, 4) != 4 || rep[0] != SOCKS5_VERSION || rep[1] != 0x00)
        return -1;
    if (rep[3] != SOCKS5_ATYP_IPV4)
        return -1;
    unsigned char ap[6];
    if (recv_n(s, (char*)ap, 6) != 6)
        return -1;

    relay_addr->sin_family = AF_INET;
    memcpy(&relay_addr->sin_addr.s_addr, ap, 4);
    memcpy(&relay_addr->sin_port, ap + 4, 2);

    return 0;
}

// Connect UDP ASSOCIATE with a SOCKS5 proxy (per proxy config). Unused while UDP is not
// proxied, kept so the UDP relay can be re-enabled later.
BOOL establish_udp_associate_for_config(PROXY_CONFIG *cfg)
{
    if (cfg == NULL || cfg->host[0] == '\0' || cfg->port == 0)
        return FALSE;
    if (cfg->type != PROXY_TYPE_SOCKS5)
        return FALSE;

    // Prevent retry spam - only try every 1 second per config
    ULONGLONG now = GetTickCount64();
    if (now - cfg->last_udp_attempt < 1000)
        return FALSE;

    cfg->last_udp_attempt = now;

    if (cfg->udp_tcp_ctrl != INVALID_SOCKET)
    {
        closesocket(cfg->udp_tcp_ctrl);
        cfg->udp_tcp_ctrl = INVALID_SOCKET;
    }
    if (cfg->udp_send_sock != INVALID_SOCKET)
    {
        closesocket(cfg->udp_send_sock);
        cfg->udp_send_sock = INVALID_SOCKET;
    }

    SOCKET tcp_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (tcp_sock == INVALID_SOCKET)
        return FALSE;

    configure_tcp_socket(tcp_sock, 262144, 3000);

    UINT32 socks5_ip = cfg->resolved_ip ? cfg->resolved_ip : resolve_hostname(cfg->host);
    if (socks5_ip == 0)
    {
        closesocket(tcp_sock);
        return FALSE;
    }

    struct sockaddr_in socks_addr;
    memset(&socks_addr, 0, sizeof(socks_addr));
    socks_addr.sin_family = AF_INET;
    socks_addr.sin_addr.s_addr = socks5_ip;
    socks_addr.sin_port = htons(cfg->port);

    if (connect_with_timeout(tcp_sock, (struct sockaddr *)&socks_addr, sizeof(socks_addr), 2000) == SOCKET_ERROR)
    {
        closesocket(tcp_sock);
        return FALSE;
    }

    if (socks5_udp_associate_with_config(tcp_sock, &cfg->udp_relay_addr, cfg) != 0)
    {
        closesocket(tcp_sock);
        return FALSE;
    }

    // Many SOCKS5 servers return 0.0.0.0 as BND.ADDR (RFC 1928: use the address of the
    // TCP control connection); sendto(0.0.0.0) fails, so use the proxy's IP instead.
    if (cfg->udp_relay_addr.sin_addr.s_addr == INADDR_ANY)
        cfg->udp_relay_addr.sin_addr.s_addr = socks5_ip;

    DWORD zero_timeout = 0;
    setsockopt(tcp_sock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&zero_timeout, sizeof(zero_timeout));
    setsockopt(tcp_sock, SOL_SOCKET, SO_SNDTIMEO, (const char*)&zero_timeout, sizeof(zero_timeout));

    // Keepalives so the proxy does not idle-close the control connection.
    BOOL ka_on = TRUE;
    setsockopt(tcp_sock, SOL_SOCKET, SO_KEEPALIVE, (const char*)&ka_on, sizeof(ka_on));
    struct tcp_keepalive ka = { 1, 10000, 2000 }; // idle 10s, retry every 2s
    DWORD ka_bytes;
    WSAIoctl(tcp_sock, SIO_KEEPALIVE_VALS, &ka, sizeof(ka), NULL, 0, &ka_bytes, NULL, NULL);

    cfg->udp_tcp_ctrl = tcp_sock;

    cfg->udp_send_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (cfg->udp_send_sock == INVALID_SOCKET)
    {
        closesocket(cfg->udp_tcp_ctrl);
        cfg->udp_tcp_ctrl = INVALID_SOCKET;
        cfg->udp_connected = FALSE;
        return FALSE;
    }

    configure_udp_socket(cfg->udp_send_sock, 262144, 30000);

    cfg->udp_connected = TRUE;
    log_message("UDP ASSOCIATE established with SOCKS5 proxy %s:%d (UDP relay at %s:%d)",
        cfg->host, cfg->port,
        inet_ntoa(cfg->udp_relay_addr.sin_addr), ntohs(cfg->udp_relay_addr.sin_port));
    return TRUE;
}
