/*
 * ProxyBridgeCore HTTP proxy: CONNECT tunnels (IPv4/IPv6).
 *
 * Ported from the ProxyBridge Windows core (v4.x),
 * Copyright (c) 2025 Anof-cyber/InterceptSuite, MIT License.
 * Modifications copyright (c) 2026 ProxyBridge Team, MIT License:
 *   the CONNECT reply is read up to the end of its headers (never past it), so a reply
 *   split across TCP segments works and tunnel bytes that follow it are not swallowed;
 *   status codes are logged; credentials are wiped from stack buffers.
 */
#include "pb_internal.h"

// Read the proxy's reply headers byte by byte until CRLFCRLF. Reading one byte at a time
// guarantees we never consume tunnel data that the proxy may send right after the
// headers (some proxies push the server's first bytes in the same segment).
// Returns the header length, or -1 on error / peer close / oversize.
static int http_read_reply_headers(SOCKET s, char *buf, int buf_size)
{
    int len = 0;
    while (len < buf_size - 1)
    {
        int r = recv(s, buf + len, 1, 0);
        if (r <= 0)
            return -1;
        len++;
        if (len >= 4 && buf[len - 4] == '\r' && buf[len - 3] == '\n' &&
            buf[len - 2] == '\r' && buf[len - 1] == '\n')
        {
            buf[len] = '\0';
            return len;
        }
    }
    return -1;   // headers larger than the buffer
}

// Parse "HTTP/1.x NNN ..." and return NNN, or -1 when the status line is malformed.
static int http_parse_status(const char *response)
{
    if (strncmp(response, "HTTP/1.", 7) != 0)
        return -1;
    const char *code_start = strchr(response, ' ');
    if (code_start == NULL)
        return -1;
    return atoi(code_start + 1);
}

// Shared CONNECT exchange for both address families. host_part is the authority without
// the port (an IPv4 literal, "[ipv6]" or a hostname).
static int http_connect_host(SOCKET s, const char *host_part, UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    char request[HTTP_BUFFER_SIZE];
    char response[4096];
    int len;
    BOOL use_auth = (cfg != NULL && cfg->username[0] != '\0');

    if (use_auth)
    {
        // "username:password" encoded as Base64 (RFC 7617)
        char credentials[SOCKS5_BUFFER_SIZE];
        char encoded[HTTP_BUFFER_SIZE];
        snprintf(credentials, sizeof(credentials), "%s:%s", cfg->username, cfg->password);
        base64_encode(credentials, encoded, sizeof(encoded));

        len = snprintf(request, sizeof(request),
            "CONNECT %s:%u HTTP/1.1\r\n"
            "Host: %s:%u\r\n"
            "Proxy-Authorization: Basic %s\r\n"
            "Proxy-Connection: keep-alive\r\n"
            "\r\n",
            host_part, dest_port, host_part, dest_port, encoded);
        SecureZeroMemory(credentials, sizeof(credentials));
        SecureZeroMemory(encoded, sizeof(encoded));
    }
    else
    {
        len = snprintf(request, sizeof(request),
            "CONNECT %s:%u HTTP/1.1\r\n"
            "Host: %s:%u\r\n"
            "Proxy-Connection: keep-alive\r\n"
            "\r\n",
            host_part, dest_port, host_part, dest_port);
    }

    if (len <= 0 || len >= (int)sizeof(request))
    {
        SecureZeroMemory(request, sizeof(request));
        log_message("HTTP: CONNECT request too long");
        return -1;
    }

    int sent = send_all(s, request, len);
    SecureZeroMemory(request, sizeof(request));
    if (sent == SOCKET_ERROR)
    {
        log_message("HTTP: Failed to send CONNECT request (WSA %d)", WSAGetLastError());
        return -1;
    }

    if (http_read_reply_headers(s, response, (int)sizeof(response)) < 0)
    {
        log_message("HTTP: No valid CONNECT reply from proxy (WSA %d)", WSAGetLastError());
        return -1;
    }

    int status_code = http_parse_status(response);
    if (status_code < 0)
    {
        log_message("HTTP: Invalid CONNECT reply format");
        return -1;
    }
    if (status_code < 200 || status_code > 299)
    {
        log_message("HTTP: CONNECT to %s:%u failed with status %d%s", host_part, dest_port, status_code,
            status_code == 407 ? " (proxy authentication required or rejected)" :
            status_code == 403 ? " (forbidden by proxy)" :
            status_code == 502 || status_code == 503 || status_code == 504 ? " (proxy could not reach the destination)" : "");
        return -1;
    }

    return 0;
}

int http_connect_v6(SOCKET s, const UINT8 dest_ip6[16], UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    // Format IPv6 address as [addr] per RFC 2732
    char addr_str[64];
    inet_ntop(AF_INET6, (void *)dest_ip6, addr_str, sizeof(addr_str));

    // Use the cached hostname only if this config opts to let the proxy resolve DNS.
    char cached_domain[256];
    char host_buf[270];  // big enough for [ipv6] or a domain
    if (cfg != NULL && cfg->send_domain_to_proxy && dns_cache_lookup_v6(dest_ip6, cached_domain, sizeof(cached_domain)))
        strncpy_s(host_buf, sizeof(host_buf), cached_domain, _TRUNCATE);
    else
        snprintf(host_buf, sizeof(host_buf), "[%s]", addr_str);

    return http_connect_host(s, host_buf, dest_port, cfg);
}

int http_connect(SOCKET s, UINT32 dest_ip, UINT16 dest_port, const PROXY_CONFIG *cfg)
{
    // Use the cached hostname only if this config opts to let the proxy resolve DNS.
    char cached_domain[256];
    char ip_str[32];
    const char *host_part;
    if (cfg != NULL && cfg->send_domain_to_proxy && dns_cache_lookup(dest_ip, cached_domain, sizeof(cached_domain)))
    {
        host_part = cached_domain;
    }
    else
    {
        format_ip_address(dest_ip, ip_str, sizeof(ip_str));
        host_part = ip_str;
    }

    return http_connect_host(s, host_part, dest_port, cfg);
}
