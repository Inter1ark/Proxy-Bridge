using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>Outcome of <see cref="ProxyChecker.CheckAsync"/>.</summary>
public sealed class ProxyCheckResult
{
    public bool Ok { get; init; }
    /// <summary>"" when Ok, else one of the <see cref="ProxyChecker"/> error codes.</summary>
    public string Error { get; init; } = "";
    /// <summary>Technical detail for the log (never contains credentials).</summary>
    public string Detail { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Country { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public string City { get; init; } = "";
    /// <summary>TCP connect time to the proxy in ms, or 0 when unknown.</summary>
    public int LatencyMs { get; init; }

    public ProxyCheckInfo ToInfo() => new()
    {
        Ok = Ok,
        Error = Error,
        Ip = Ip,
        Country = Country,
        CountryCode = CountryCode,
        City = City,
        LatencyMs = LatencyMs,
        CheckedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    };
}

/// <summary>
/// Real check of an HTTP or SOCKS5 proxy: an HTTP request through the proxy (HttpClient with
/// WebProxy "http://host:port" or "socks5://host:port" and NetworkCredential) to ip-api.com, with
/// ipinfo.io as a fallback. Returns the exit IP and its location, or an error code. This runs in the
/// GUI process, which the core never intercepts, so it works while connected and does not touch
/// the system's traffic.
/// </summary>
public static class ProxyChecker
{
    public const string ErrAuth = "auth_failed";
    public const string ErrUnreachable = "unreachable";
    public const string ErrTimeout = "timeout";
    public const string ErrBadResponse = "bad_response";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Overridable for tests (a local fake echo server).</summary>
    public static string PrimaryUrl { get; set; } =
        "http://ip-api.com/json/?fields=status,message,country,countryCode,city,query&lang={lang}";
    public static string FallbackUrl { get; set; } = "https://ipinfo.io/json";

    public static async Task<ProxyCheckResult> CheckAsync(ParsedProxy p, string lang = "en", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Host) || !int.TryParse(p.Port, out var port) || port <= 0 || port > 65535)
            return new ProxyCheckResult { Error = ErrBadResponse, Detail = "invalid proxy address" };

        var latencyTask = LatencyProbe.MeasureAsync(p.Host, port, 5000);

        var scheme = p.IsHttp ? "http" : "socks5";
        var webProxy = new WebProxy($"{scheme}://{FormatHost(p.Host)}:{port}") { BypassProxyOnLocal = false };
        if (p.HasAuth)
            webProxy.Credentials = new NetworkCredential(p.User, p.Pass);

        using var handler = new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = Timeout,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyBridge/" + LicenseService.AppVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var primary = await TryOnce(client, PrimaryUrl.Replace("{lang}", lang == "ru" ? "ru" : "en"), ParseIpApi, ct);
        var result = primary;
        // Fallback only when the proxy itself answered (auth and connection errors would repeat).
        if (!primary.Ok && primary.Error == ErrBadResponse && !string.IsNullOrEmpty(FallbackUrl))
        {
            var fallback = await TryOnce(client, FallbackUrl, ParseIpInfo, ct);
            if (fallback.Ok || fallback.Error != ErrBadResponse) result = fallback;
        }

        int latency = 0;
        if (result.Ok)
        {
            try
            {
                latency = await latencyTask ?? 0;
            }
            catch
            {
                // the probe never throws; keep 0 (unknown) just in case
            }
        }

        return new ProxyCheckResult
        {
            Ok = result.Ok,
            Error = result.Error,
            Detail = result.Detail,
            Ip = result.Ip,
            Country = result.Country,
            CountryCode = result.CountryCode,
            City = result.City,
            LatencyMs = result.Ok ? latency : 0
        };
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    private static async Task<ProxyCheckResult> TryOnce(HttpClient client, string url,
        Func<JsonElement, ProxyCheckResult?> parse, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return new ProxyCheckResult { Error = ErrAuth, Detail = "HTTP 407 from proxy" };
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                return new ProxyCheckResult { Error = ErrBadResponse, Detail = $"HTTP {(int)response.StatusCode} from {Host(url)}" };
            JsonElement json;
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException)
            {
                return new ProxyCheckResult { Error = ErrBadResponse, Detail = $"not JSON from {Host(url)}" };
            }
            return parse(json) ?? new ProxyCheckResult { Error = ErrBadResponse, Detail = $"unexpected JSON from {Host(url)}" };
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return new ProxyCheckResult { Error = ErrTimeout, Detail = $"no answer in {Timeout.TotalSeconds:0} s" };
        }
        catch (HttpRequestException ex)
        {
            return new ProxyCheckResult { Error = Classify(ex), Detail = Flatten(ex) };
        }
        catch (Exception ex)
        {
            return new ProxyCheckResult { Error = ErrBadResponse, Detail = Flatten(ex) };
        }
    }

    /// <summary>Maps a transport error to an error code.</summary>
    internal static string Classify(HttpRequestException ex)
    {
        var text = Flatten(ex).ToLowerInvariant();
        if (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired || text.Contains("407")
            || text.Contains("authenticat") || text.Contains("credentials"))
            return ErrAuth;

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException se)
            {
                return se.SocketErrorCode == SocketError.TimedOut ? ErrTimeout : ErrUnreachable;
            }
            if (e is TimeoutException) return ErrTimeout;
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => ErrUnreachable,
            HttpRequestError.ConnectionError => ErrUnreachable,
            _ => ErrBadResponse
        };
    }

    private static string Flatten(Exception ex)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (Exception? e = ex; e != null && parts.Count < 4; e = e.InnerException)
            parts.Add(e.Message);
        return AppLog.Sanitize(string.Join(" / ", parts));
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    /// <summary>ip-api.com: {"status":"success","country":"Germany","countryCode":"DE","city":"Frankfurt","query":"1.2.3.4"}</summary>
    internal static ProxyCheckResult? ParseIpApi(JsonElement j)
    {
        var ip = Str(j, "query");
        if (Str(j, "status") != "success" || ip.Length == 0) return null;
        return new ProxyCheckResult
        {
            Ok = true,
            Ip = ip,
            Country = Str(j, "country"),
            CountryCode = Str(j, "countryCode").ToUpperInvariant(),
            City = Str(j, "city")
        };
    }

    /// <summary>ipinfo.io: {"ip":"1.2.3.4","city":"Frankfurt am Main","country":"DE"}</summary>
    internal static ProxyCheckResult? ParseIpInfo(JsonElement j)
    {
        var ip = Str(j, "ip");
        if (ip.Length == 0) return null;
        var cc = Str(j, "country").ToUpperInvariant();
        return new ProxyCheckResult
        {
            Ok = true,
            Ip = ip,
            CountryCode = cc.Length == 2 ? cc : "",
            Country = CountryName(cc),
            City = Str(j, "city")
        };
    }

    private static string CountryName(string cc)
    {
        if (cc.Length != 2) return "";
        try
        {
            return new System.Globalization.RegionInfo(cc).EnglishName;
        }
        catch (ArgumentException)
        {
            return cc;
        }
    }
}
