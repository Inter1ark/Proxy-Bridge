using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// SX.ORG API Client — bulletproof version, all methods wrapped in try-catch
/// </summary>
public class SxOrgApiService
{
    private readonly HttpClient _client;
    private string _apiKey = "";
    private const string BaseUrl = "https://api.sx.org";
    
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "ProxyBridge_SX_Log.txt"
    );

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SxOrgApiService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.Add("User-Agent", "ProxyBridge/3.0");
        Log("=== SX.ORG API Client Initialized ===");
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey ?? "";
        Log($"API Key set: {_apiKey.Substring(0, Math.Min(10, _apiKey.Length))}...");
    }

    // ========================================================================
    // GET helper — all network calls go through here
    // ========================================================================
    private async Task<string?> GetJsonAsync(string url)
    {
        try
        {
            Log($"GET {url.Replace(_apiKey, "***")}");
            var resp = await _client.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            Log($"Response: {resp.StatusCode}, Length: {json.Length}");
            Log($"Body: {(json.Length > 300 ? json.Substring(0, 300) + "..." : json)}");
            return json;
        }
        catch (Exception ex)
        {
            Log($"HTTP GET ERROR: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PostJsonAsync(string url, string body)
    {
        try
        {
            Log($"POST {url.Replace(_apiKey, "***")}");
            Log($"Body: {body}");
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync(url, content);
            var json = await resp.Content.ReadAsStringAsync();
            Log($"Response: {resp.StatusCode}");
            Log($"Body: {(json.Length > 500 ? json.Substring(0, 500) + "..." : json)}");
            return json;
        }
        catch (Exception ex)
        {
            Log($"HTTP POST ERROR: {ex.Message}");
            return null;
        }
    }

    // Safe deserialize — never throws
    private T? Safe<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (Exception ex) { Log($"JSON parse error: {ex.Message}"); return null; }
    }

    // ========================================================================
    // VALIDATE KEY & GET BALANCE
    // ========================================================================
    public async Task<(bool isValid, string balance)> ValidateKeyAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
            return (false, "0.00");

        try
        {
            // 1. Check plan/info
            var planJson = await GetJsonAsync($"{BaseUrl}/v2/plan/info?apiKey={_apiKey}");
            if (planJson == null) return (false, "0.00");

            // Just check if "success":true exists
            if (!planJson.Contains("\"success\":true"))
                return (false, "0.00");

            // 2. Get balance
            var balJson = await GetJsonAsync($"{BaseUrl}/v2/user/balance?apiKey={_apiKey}");
            if (balJson == null) return (false, "0.00");

            // Parse balance manually to avoid decimal locale issues
            var balData = Safe<BalanceResponse>(balJson);
            if (balData?.Success == true)
            {
                var bal = balData.Balance.ToString("F2");
                Log($"✓ Validated! Balance: ${bal}");
                return (true, bal);
            }

            return (false, "0.00");
        }
        catch (Exception ex)
        {
            Log($"ERROR ValidateKey: {ex.Message}");
            return (false, "0.00");
        }
    }

    // ========================================================================
    // COUNTRIES
    // ========================================================================
    public async Task<List<SxCountry>> GetCountriesAsync()
    {
        try
        {
            var json = await GetJsonAsync($"{BaseUrl}/v2/dir/countries?apiKey={_apiKey}");
            var data = Safe<CountriesResponse>(json);
            if (data?.Success == true && data.Countries != null)
            {
                Log($"✓ Loaded {data.Countries.Count} countries");
                return data.Countries;
            }
        }
        catch (Exception ex) { Log($"ERROR GetCountries: {ex.Message}"); }
        return new List<SxCountry>();
    }

    // ========================================================================
    // STATES
    // ========================================================================
    public async Task<List<SxState>> GetStatesAsync(int countryId)
    {
        try
        {
            var json = await GetJsonAsync($"{BaseUrl}/v2/dir/states?apiKey={_apiKey}&countryId={countryId}");
            var data = Safe<StatesResponse>(json);
            if (data?.Success == true && data.States != null)
            {
                Log($"✓ Loaded {data.States.Count} states");
                return data.States;
            }
        }
        catch (Exception ex) { Log($"ERROR GetStates: {ex.Message}"); }
        return new List<SxState>();
    }

    // ========================================================================
    // CITIES
    // ========================================================================
    public async Task<List<SxCity>> GetCitiesAsync(int countryId, int stateId)
    {
        try
        {
            var json = await GetJsonAsync($"{BaseUrl}/v2/dir/cities?apiKey={_apiKey}&countryId={countryId}&stateId={stateId}");
            var data = Safe<CitiesResponse>(json);
            if (data?.Success == true && data.Cities != null)
            {
                Log($"✓ Loaded {data.Cities.Count} cities");
                return data.Cities;
            }
        }
        catch (Exception ex) { Log($"ERROR GetCities: {ex.Message}"); }
        return new List<SxCity>();
    }

    // ========================================================================
    // GET EXISTING PROXY PORTS
    // ========================================================================
    public async Task<List<SxProxyPort>> GetProxyPortsAsync()
    {
        try
        {
            var json = await GetJsonAsync($"{BaseUrl}/v2/proxy/ports?apiKey={_apiKey}&per_page=100");
            if (json == null) return new List<SxProxyPort>();

            // Parse with JsonDocument to handle dynamic "status" field
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
                return new List<SxProxyPort>();

            if (!root.TryGetProperty("message", out var msgEl))
                return new List<SxProxyPort>();

            if (!msgEl.TryGetProperty("proxies", out var proxiesEl))
                return new List<SxProxyPort>();

            var result = new List<SxProxyPort>();
            foreach (var p in proxiesEl.EnumerateArray())
            {
                try
                {
                    var proxy = new SxProxyPort
                    {
                        Id = p.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0,
                        Name = p.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                        Login = p.TryGetProperty("login", out var loginEl) ? loginEl.GetString() ?? "" : "",
                        Password = p.TryGetProperty("password", out var passEl) ? passEl.GetString() ?? "" : "",
                        CountryCode = p.TryGetProperty("countryCode", out var ccEl) ? ccEl.GetString() ?? "" : "",
                        Country = p.TryGetProperty("country", out var countryEl) ? countryEl.GetString() : null,
                    };

                    // Parse server and port from "proxy" field: "ip:port"
                    if (p.TryGetProperty("proxy", out var proxyEl))
                    {
                        var proxyStr = proxyEl.GetString() ?? "";
                        var parts = proxyStr.Split(':');
                        if (parts.Length >= 2)
                        {
                            proxy.Server = parts[0];
                            int.TryParse(parts[1], out var port);
                            proxy.Port = port;
                        }
                    }

                    // status can be string, number, object — handle all
                    if (p.TryGetProperty("status", out var statusEl))
                    {
                        proxy.Status = statusEl.ValueKind switch
                        {
                            JsonValueKind.String => statusEl.GetString() ?? "unknown",
                            JsonValueKind.Number => statusEl.GetInt32() == 1 ? "active" : "inactive",
                            _ => "unknown"
                        };
                    }

                    result.Add(proxy);
                }
                catch (Exception ex)
                {
                    Log($"WARNING: Failed to parse proxy entry: {ex.Message}");
                }
            }

            Log($"✓ Loaded {result.Count} proxies from API");
            return result;
        }
        catch (Exception ex)
        {
            Log($"ERROR GetProxyPorts: {ex.Message}");
            return new List<SxProxyPort>();
        }
    }

    // ========================================================================
    // CREATE PROXY
    // ========================================================================
    public async Task<List<SxProxyPort>> CreateProxyAsync(
        string countryCode, int typeId, int proxyTypeId,
        string? proxyName = null, string? state = null, string? city = null)
    {
        try
        {
            var url = $"{BaseUrl}/v2/proxy/create-port?apiKey={_apiKey}";
            var reqName = proxyName ?? $"ProxyBridge_{DateTime.Now:HHmmss}";

            var body = JsonSerializer.Serialize(new
            {
                country_code = countryCode,
                state = state,
                city = city,
                type_id = typeId,
                proxy_type_id = proxyTypeId,
                server_port_type_id = 0,
                name = reqName,
                count = 1,
                ttl = 60,
                traffic_limit = 0
            });

            var json = await PostJsonAsync(url, body);
            if (json == null) return new List<SxProxyPort>();

            // Parse response manually for safety
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
            {
                var errMsg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "unknown error";
                Log($"Create failed: {errMsg}");
                return new List<SxProxyPort>();
            }

            if (!root.TryGetProperty("data", out var dataEl))
                return new List<SxProxyPort>();

            var result = new List<SxProxyPort>();
            foreach (var d in dataEl.EnumerateArray())
            {
                try
                {
                    result.Add(new SxProxyPort
                    {
                        Name = reqName,
                        Login = d.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "",
                        Password = d.TryGetProperty("password", out var pw) ? pw.GetString() ?? "" : "",
                        Server = d.TryGetProperty("server", out var s) ? s.GetString() ?? "" : "",
                        Port = d.TryGetProperty("port", out var pt) ? pt.GetInt32() : 0,
                        CountryCode = countryCode,
                        Country = d.TryGetProperty("country", out var c) ? c.GetString() : null,
                        Status = "active"
                    });
                }
                catch (Exception ex)
                {
                    Log($"WARNING: Failed to parse created proxy: {ex.Message}");
                }
            }

            Log($"✓ Created {result.Count} proxies!");
            return result;
        }
        catch (Exception ex)
        {
            Log($"ERROR CreateProxy: {ex.Message}\n{ex.StackTrace}");
            return new List<SxProxyPort>();
        }
    }
}

// ============================================================================
// DATA MODELS — simple, no fancy deserialization that could crash
// ============================================================================

public class BalanceResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("balance")] public decimal Balance { get; set; }
}

public class CountriesResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("countries")] public List<SxCountry>? Countries { get; set; }
}

public class SxCountry
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public class StatesResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("states")] public List<SxState>? States { get; set; }
}

public class SxState
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public class CitiesResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("cities")] public List<SxCity>? Cities { get; set; }
}

public class SxCity
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    public override string ToString() => Name;
}

// Proxy port — used for both listing and creating
public class SxProxyPort
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string? Country { get; set; }
    public string Status { get; set; } = "unknown";
}
