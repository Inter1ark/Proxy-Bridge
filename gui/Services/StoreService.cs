using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>Error from the store API: Code is the server error code (or "network").</summary>
public sealed class StoreException : Exception
{
    public string Code { get; }
    public StoreException(string code) : base(code) { Code = code; }
}

public sealed class StoreCountry
{
    public string Code { get; init; } = "";
    public string Ru { get; init; } = "";
    public string En { get; init; } = "";
    public int Id { get; init; }
    public string Name => I18n.Instance.Language == "en" ? En : Ru;
}

public sealed class StoreGeo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed class StoreCatalog
{
    public int DcPriceRub { get; init; }
    public int DcDays { get; init; }
    public List<StoreCountry> DcCountries { get; init; } = new();
    public bool HasDc => DcCountries.Count > 0;

    public Dictionary<string, double> GbPriceRub { get; init; } = new();
    public int MinGb { get; init; } = 1;
    public int MaxGb { get; init; } = 100;
    public int TtlMax { get; init; } = 1440;
    public List<StoreCountry> TrafficCountries { get; init; } = new();
    public bool HasTraffic => TrafficCountries.Count > 0 && GbPriceRub.Count > 0;
}

public sealed class StoreProxyInfo
{
    public int Id { get; init; }
    public string Kind { get; init; } = "";          // dc | traffic
    public string Status { get; init; } = "";        // provisioning | active | exhausted | expired
    public string Type { get; init; } = "";          // mobile | residential | datacenter
    public StoreCountry Country { get; init; } = new();
    public string? State { get; init; }
    public string? City { get; init; }
    public string? Rotation { get; init; }
    public int? Ttl { get; init; }
    public int? GbTotal { get; init; }
    public double? GbUsed { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool RenewPending { get; init; }
    public bool CanRenew { get; init; }
    public bool CanTopup { get; init; }
    public bool CanRefreshIp { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Login { get; init; } = "";
    public string Password { get; init; } = "";

    public bool IsReady => Host.Length > 0 && Port > 0;

    /// <summary>Connection string for the proxy list (SOCKS5 works on every store proxy).</summary>
    public string ProxyUrl => $"socks5://{Uri.EscapeDataString(Login)}:{Uri.EscapeDataString(Password)}@{Host}:{Port}";
}

public sealed class StoreOrderStatus
{
    public string Token { get; init; } = "";
    public string State { get; init; } = "";         // pending | processing | done | failed | canceled
    public int AmountRub { get; init; }
    public bool Refunded { get; init; }
    public StoreProxyInfo? Proxy { get; init; }
}

/// <summary>
/// Client of the in-app proxy store (server side: /api/store). Every call carries the license key
/// and the device id; vendor keys never leave the server.
/// </summary>
public sealed class StoreService
{
    public static StoreService Instance { get; } = new();

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string _base;

    private StoreService()
    {
        var lic = LicenseService.Instance.ApiBase;
        _base = lic.EndsWith("/license", StringComparison.OrdinalIgnoreCase)
            ? lic[..^"/license".Length] + "/store"
            : lic.TrimEnd('/') + "/../store";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyBridge/" + LicenseService.AppVersion);
        return client;
    }

    private async Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken ct = default)
    {
        var state = LicenseService.Instance.GetCachedState();
        body["key"] = state.Key;
        body["hwid"] = LicenseService.Instance.GetHwid();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(_base + path, content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"store {path}: {ex.GetType().Name}");
            throw new StoreException("network");
        }
        using (response)
        {
            JsonObject? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }
            if (!response.IsSuccessStatusCode || json == null || json["ok"]?.GetValue<bool>() != true)
            {
                var code = json?["error"]?.GetValue<string>() ?? "server";
                AppLog.Warn($"store {path}: {(int)response.StatusCode} {code}");
                throw new StoreException(code);
            }
            return json;
        }
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static int? IntN(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static double? Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static StoreCountry Country(JsonNode? n) => new()
    {
        Code = Str(n?["code"]),
        Ru = Str(n?["ru"]),
        En = Str(n?["en"]),
        Id = Int(n?["id"])
    };

    private static List<StoreCountry> Countries(JsonNode? arr)
    {
        var list = new List<StoreCountry>();
        if (arr is JsonArray a)
            foreach (var c in a) list.Add(Country(c));
        return list;
    }

    public async Task<StoreCatalog> GetCatalogAsync(CancellationToken ct = default)
    {
        var j = await PostAsync("/catalog", new JsonObject(), ct).ConfigureAwait(false);
        var dc = j["dc"] as JsonObject;
        var tr = j["traffic"] as JsonObject;
        var prices = new Dictionary<string, double>();
        if (tr?["gb_price_rub"] is JsonObject p)
            foreach (var kv in p)
                if (Dbl(kv.Value) is double d) prices[kv.Key] = d;
        return new StoreCatalog
        {
            DcPriceRub = Int(dc?["price_rub"]),
            DcDays = Int(dc?["days"]),
            DcCountries = Countries(dc?["countries"]),
            GbPriceRub = prices,
            MinGb = Math.Max(1, Int(tr?["min_gb"])),
            MaxGb = Math.Max(1, Int(tr?["max_gb"])),
            TtlMax = Math.Max(1, Int(tr?["ttl_max"])),
            TrafficCountries = Countries(tr?["countries"])
        };
    }

    private static List<StoreGeo> Geo(JsonNode? arr)
    {
        var list = new List<StoreGeo>();
        if (arr is JsonArray a)
            foreach (var g in a) list.Add(new StoreGeo { Id = Int(g?["id"]), Name = Str(g?["name"]) });
        return list;
    }

    public async Task<List<StoreGeo>> GetStatesAsync(int countryId, CancellationToken ct = default)
    {
        var j = await PostAsync("/states", new JsonObject { ["country_id"] = countryId }, ct).ConfigureAwait(false);
        return Geo(j["states"]);
    }

    public async Task<List<StoreGeo>> GetCitiesAsync(int countryId, int stateId, CancellationToken ct = default)
    {
        var j = await PostAsync("/cities", new JsonObject { ["country_id"] = countryId, ["state_id"] = stateId }, ct)
            .ConfigureAwait(false);
        return Geo(j["cities"]);
    }

    public async Task<bool> IsAvailableAsync(string countryCode, string type, CancellationToken ct = default)
    {
        var j = await PostAsync("/availability", new JsonObject { ["country"] = countryCode, ["type"] = type }, ct)
            .ConfigureAwait(false);
        return Bool(j["available"]);
    }

    public async Task<int> QuoteAsync(string product, JsonObject parameters, CancellationToken ct = default)
    {
        var j = await PostAsync("/quote", new JsonObject { ["product"] = product, ["params"] = parameters }, ct)
            .ConfigureAwait(false);
        return Int(j["amount_rub"]);
    }

    /// <summary>Creates an order and returns (token, payment URL, amount).</summary>
    public async Task<(string token, string payUrl, int amount)> CreateOrderAsync(string product, JsonObject parameters,
        string method, CancellationToken ct = default)
    {
        var j = await PostAsync("/order", new JsonObject
        {
            ["product"] = product, ["params"] = parameters, ["method"] = method
        }, ct).ConfigureAwait(false);
        return (Str(j["token"]), Str(j["pay_url"]), Int(j["amount_rub"]));
    }

    public async Task<StoreOrderStatus> GetOrderAsync(string token, CancellationToken ct = default)
    {
        var j = await PostAsync("/order/status", new JsonObject { ["token"] = token }, ct).ConfigureAwait(false);
        return new StoreOrderStatus
        {
            Token = Str(j["token"]),
            State = Str(j["state"]),
            AmountRub = Int(j["amount_rub"]),
            Refunded = Bool(j["refunded"]),
            Proxy = j["proxy"] is JsonObject p ? Proxy(p) : null
        };
    }

    public async Task<List<StoreProxyInfo>> GetProxiesAsync(CancellationToken ct = default)
    {
        var j = await PostAsync("/proxies", new JsonObject(), ct).ConfigureAwait(false);
        var list = new List<StoreProxyInfo>();
        if (j["proxies"] is JsonArray a)
            foreach (var p in a)
                if (p is JsonObject o) list.Add(Proxy(o));
        return list;
    }

    public Task RefreshIpAsync(int proxyId, CancellationToken ct = default) =>
        PostAsync("/proxy/refresh-ip", new JsonObject { ["proxy_id"] = proxyId }, ct);

    private static StoreProxyInfo Proxy(JsonObject o)
    {
        DateTime? expires = null;
        if (DateTime.TryParse(Str(o["expires_at"]), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var e))
            expires = e;
        return new StoreProxyInfo
        {
            Id = Int(o["id"]),
            Kind = Str(o["kind"]),
            Status = Str(o["status"]),
            Type = Str(o["type"]),
            Country = Country(o["country"]),
            State = Str(o["state"]) is { Length: > 0 } s ? s : null,
            City = Str(o["city"]) is { Length: > 0 } c ? c : null,
            Rotation = Str(o["rotation"]) is { Length: > 0 } r ? r : null,
            Ttl = IntN(o["ttl"]),
            GbTotal = IntN(o["gb_total"]),
            GbUsed = Dbl(o["gb_used"]),
            ExpiresAt = expires,
            RenewPending = Bool(o["renew_pending"]),
            CanRenew = Bool(o["can_renew"]),
            CanTopup = Bool(o["can_topup"]),
            CanRefreshIp = Bool(o["can_refresh_ip"]),
            Host = Str(o["host"]),
            Port = Int(o["port"]),
            Login = Str(o["login"]),
            Password = Str(o["password"])
        };
    }
}
