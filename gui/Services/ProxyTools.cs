using System;
using System.Linq;

namespace ProxyBridge.GUI.Services;

/// <summary>A proxy string split into parts (see <see cref="ProxyParser"/>).</summary>
public sealed class ParsedProxy
{
    public string Type { get; set; } = "SOCKS5";
    public string Host { get; set; } = "";
    public string Port { get; set; } = "";
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";

    public string HostPort => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    public bool HasAuth => User.Length > 0 || Pass.Length > 0;
    public bool IsHttp => string.Equals(Type, "HTTP", StringComparison.OrdinalIgnoreCase);

    /// <summary>Credentials with the password hidden: "user:••••" (or "••••" for a password-only proxy).</summary>
    public string MaskedAuth => !HasAuth ? "" : (User.Length > 0 ? $"{User}:••••" : "••••");

    /// <summary>Short login for lists: long usernames are cut ("very-long-user-name…:••••").</summary>
    public string ShortAuth => !HasAuth ? "" : (User.Length > 0 ? $"{Shorten(User, 8)}:••••" : "••••");

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}

/// <summary>
/// Accepted formats: socks5://user:pass@host:port (also socks5h://, socks://), http(s)://user:pass@host:port,
/// user:pass@host:port, host:port@user:pass, host:port, host:port:pass, host:port:user:pass
/// (password may contain ':'). Formats without a scheme are SOCKS5.
/// </summary>
public static class ProxyParser
{
    public static bool TryParse(string? input, out ParsedProxy proxy)
    {
        proxy = new ParsedProxy();
        try
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0 || s.Contains(' ') || s.Contains('	'))
                return false;

            var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                var scheme = s.Substring(0, schemeEnd).ToLowerInvariant();
                proxy.Type = scheme switch
                {
                    "http" or "https" => "HTTP",
                    "socks5" or "socks5h" or "socks" => "SOCKS5",
                    _ => ""
                };
                if (proxy.Type.Length == 0) return false;
                s = s.Substring(schemeEnd + 3).TrimEnd('/');
            }
            else
            {
                proxy.Type = "SOCKS5";
            }

            var at = s.LastIndexOf('@');
            if (at >= 0)
            {
                // user:pass@host:port (also host:port@user:pass)
                var left = s.Substring(0, at);
                var right = s.Substring(at + 1);
                if (!SplitHostPort(right, out var host, out var port) && SplitHostPort(left, out host, out port))
                    (left, right) = (right, left);
                if (!SplitHostPort(right, out host, out port)) return false;
                proxy.Host = host;
                proxy.Port = port;
                var colon = left.IndexOf(':');
                proxy.User = Unescape(colon >= 0 ? left.Substring(0, colon) : left);
                proxy.Pass = Unescape(colon >= 0 ? left.Substring(colon + 1) : "");
                return true;
            }

            if (s.StartsWith("[", StringComparison.Ordinal))
                return SplitHostPort(s, out var h6, out var p6) && Assign(proxy, h6, p6);

            // host:port, host:port:pass, host:port:user:pass (password may contain ':')
            var parts = s.Split(':');
            if (parts.Length < 2) return false;
            proxy.Host = parts[0].Trim();
            proxy.Port = parts[1].Trim();
            if (parts.Length == 3)
            {
                proxy.Pass = parts[2].Trim();
            }
            else if (parts.Length >= 4)
            {
                proxy.User = parts[2].Trim();
                proxy.Pass = string.Join(":", parts.Skip(3)).Trim();
            }
            return proxy.Host.Length > 0 && ushort.TryParse(proxy.Port, out var pn) && pn > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool Assign(ParsedProxy p, string host, string port)
    {
        p.Host = host;
        p.Port = port;
        return true;
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    /// <summary>"host:port" or "[v6]:port" with a valid port 1..65535.</summary>
    private static bool SplitHostPort(string s, out string host, out string port)
    {
        host = "";
        port = "";
        s = s.Trim();
        int colon;
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            var close = s.IndexOf(']');
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != ':') return false;
            host = s.Substring(1, close - 1);
            port = s.Substring(close + 2);
        }
        else
        {
            colon = s.LastIndexOf(':');
            if (colon <= 0 || s.IndexOf(':') != colon) return false;
            host = s.Substring(0, colon);
            port = s.Substring(colon + 1);
        }
        return host.Length > 0 && ushort.TryParse(port, out var n) && n > 0;
    }

    /// <summary>Short display form without the password: "SOCKS5  1.2.3.4:1080  user:••••".</summary>
    public static string Describe(string? raw)
    {
        if (!TryParse(raw, out var p)) return Mask(raw);
        var s = $"{p.Type}  {p.HostPort}";
        return p.HasAuth ? $"{s}  {p.MaskedAuth}" : s;
    }

    /// <summary>Hides the password inside an arbitrary proxy string (best effort).</summary>
    public static string Mask(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        var at = s.LastIndexOf('@');
        if (at > 0)
        {
            var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            var start = schemeEnd >= 0 ? schemeEnd + 3 : 0;
            var creds = s.Substring(start, at - start);
            var colon = creds.IndexOf(':');
            var user = colon >= 0 ? creds.Substring(0, colon) : creds;
            return s.Substring(0, start) + user + ":••••" + s.Substring(at);
        }
        return s;
    }
}
