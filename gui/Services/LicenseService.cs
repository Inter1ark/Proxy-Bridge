using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ProxyBridge.GUI.Services;

/// <summary>Locally cached license information (mirrors the license fields of AppConfig).</summary>
public class LicenseState
{
    public string Key { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public string Hwid { get; set; } = "";

    public bool HasKey => !string.IsNullOrWhiteSpace(Key);

    public TimeSpan CacheAge => LastCheckUtc.HasValue
        ? DateTime.UtcNow - LastCheckUtc.Value
        : TimeSpan.MaxValue;

    public bool IsExpiredLocally => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    public string PlanDisplayName => LicenseService.PlanToDisplayName(Plan);

    public string ExpiresDisplay => ExpiresAt.HasValue
        ? ExpiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        : "бессрочно";

    public string MaskedKey => LicenseService.MaskKey(Key);
}

/// <summary>Result of an activate / verify call.</summary>
public class LicenseResult
{
    public bool Ok { get; set; }
    /// <summary>Server error code (not_found, expired, device_limit, revoked, not_activated, bad_request) or "network".</summary>
    public string Error { get; set; } = "";
    public string Message { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public int ActivatedDevices { get; set; }
    public int MaxDevices { get; set; }

    public bool IsNetworkError => !Ok && Error == "network";
}

/// <summary>What the app should do at startup.</summary>
public enum LicenseStartupDecision
{
    /// <summary>No key or cache unusable: show the activation window.</summary>
    NotLicensed,
    /// <summary>Cache is fresh: open the main window and verify in the background.</summary>
    LicensedCached,
    /// <summary>Key present but cache is stale or locally expired: must verify online before opening the main window.</summary>
    NeedsOnlineVerify
}

// ---- API DTOs (source generated, see LicenseJsonContext) ----

public class LicenseActivateRequest
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("hwid")] public string Hwid { get; set; } = "";
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
}

public class LicenseDeactivateRequest
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("hwid")] public string Hwid { get; set; } = "";
}

public class LicenseApiResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("activated_devices")] public int? ActivatedDevices { get; set; }
    [JsonPropertyName("max_devices")] public int? MaxDevices { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LicenseActivateRequest))]
[JsonSerializable(typeof(LicenseDeactivateRequest))]
[JsonSerializable(typeof(LicenseApiResponse))]
internal partial class LicenseJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Client side of the ProxyBridge license API (docs/LICENSE_API.md).
/// Persists the license cache in AppConfig via ConfigManager.
/// </summary>
public class LicenseService
{
    public const string DefaultApiBase = "https://www.proxybridge.org/api/license";
    public const string BuyUrl = "https://www.proxybridge.org/buy.html";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private static readonly Lazy<LicenseService> _instance = new(() => new LicenseService());
    public static LicenseService Instance => _instance.Value;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _lock = new();
    private string? _hwid;

    public string ApiBase { get; }

    public LicenseService()
    {
        var env = Environment.GetEnvironmentVariable("PROXYBRIDGE_API_BASE");
        ApiBase = string.IsNullOrWhiteSpace(env) ? DefaultApiBase : env.Trim().TrimEnd('/');
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyBridge/" + AppVersion);
        return client;
    }

    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "3.2.0";
        }
    }

    // ------------------------------------------------------------------
    // Cached state
    // ------------------------------------------------------------------

    public LicenseState GetCachedState()
    {
        var config = ConfigManager.LoadConfig();
        return new LicenseState
        {
            Key = config.LicenseKey ?? "",
            Plan = config.LicensePlan ?? "",
            ExpiresAt = ParseIso(config.LicenseExpiresAt),
            LastCheckUtc = ParseIso(config.LicenseLastCheckUtc),
            Hwid = config.LicenseHwid ?? ""
        };
    }

    /// <summary>
    /// Startup rule:
    /// key empty -> NotLicensed;
    /// cache within 7 days and not locally expired -> LicensedCached (verify in background);
    /// otherwise -> NeedsOnlineVerify.
    /// </summary>
    public LicenseStartupDecision GetStartupDecision()
    {
        var state = GetCachedState();
        if (!state.HasKey) return LicenseStartupDecision.NotLicensed;
        if (state.CacheAge <= CacheLifetime && !state.IsExpiredLocally)
            return LicenseStartupDecision.LicensedCached;
        return LicenseStartupDecision.NeedsOnlineVerify;
    }

    /// <summary>Removes the license from the local cache (the HWID is kept).</summary>
    public void ClearLicense()
    {
        lock (_lock)
        {
            var config = ConfigManager.LoadConfig();
            config.LicenseKey = "";
            config.LicensePlan = "";
            config.LicenseExpiresAt = "";
            config.LicenseLastCheckUtc = "";
            ConfigManager.SaveConfig(config);
        }
    }

    private void SaveLicense(string key, LicenseResult result)
    {
        lock (_lock)
        {
            var config = ConfigManager.LoadConfig();
            config.LicenseKey = key;
            config.LicensePlan = result.Plan ?? "";
            config.LicenseExpiresAt = result.ExpiresAt.HasValue
                ? result.ExpiresAt.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "";
            config.LicenseLastCheckUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            config.LicenseHwid = GetHwid();
            ConfigManager.SaveConfig(config);
        }
    }

    // ------------------------------------------------------------------
    // API calls
    // ------------------------------------------------------------------

    public async Task<LicenseResult> ActivateAsync(string key)
    {
        var normalized = NormalizeKey(key);
        if (!IsValidKeyFormat(normalized))
            return Fail("bad_request");

        var result = await PostLicenseAsync("activate", normalized);
        if (result.Ok)
            SaveLicense(normalized, result);
        return result;
    }

    /// <summary>
    /// Verifies the cached key online. On ok:false (any server error) the local cache is cleared.
    /// On a network error the cache is left untouched; the caller decides by cache age.
    /// </summary>
    public async Task<LicenseResult> VerifyAsync()
    {
        var state = GetCachedState();
        if (!state.HasKey)
            return Fail("not_found");

        var result = await PostLicenseAsync("verify", state.Key);
        if (result.Ok)
            SaveLicense(state.Key, result);
        else if (!result.IsNetworkError)
            ClearLicense();
        return result;
    }

    /// <summary>
    /// Full startup check for the "stale cache" path: verifies online, and on a network error
    /// allows the app only if the cache is younger than 7 days and not locally expired.
    /// </summary>
    public async Task<LicenseResult> VerifyForStartupAsync()
    {
        var state = GetCachedState();
        var result = await VerifyAsync();
        if (result.IsNetworkError && state.HasKey && state.CacheAge < CacheLifetime && !state.IsExpiredLocally)
        {
            return new LicenseResult
            {
                Ok = true,
                Plan = state.Plan,
                ExpiresAt = state.ExpiresAt
            };
        }
        return result;
    }

    public async Task DeactivateAsync()
    {
        var state = GetCachedState();
        if (!state.HasKey) return;

        try
        {
            var body = JsonSerializer.Serialize(
                new LicenseDeactivateRequest { Key = state.Key, Hwid = GetHwid() },
                LicenseJsonContext.Default.LicenseDeactivateRequest);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(ApiBase + "/deactivate", content);
            // The response is informational only; the local license is cleared by the caller regardless.
            _ = response.StatusCode;
        }
        catch
        {
            // Network failure: the server slot stays busy, but the local device is unlinked anyway.
        }
    }

    private async Task<LicenseResult> PostLicenseAsync(string endpoint, string key)
    {
        try
        {
            var body = JsonSerializer.Serialize(
                new LicenseActivateRequest { Key = key, Hwid = GetHwid(), AppVersion = AppVersion },
                LicenseJsonContext.Default.LicenseActivateRequest);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(ApiBase + "/" + endpoint, content);
            var text = await response.Content.ReadAsStringAsync();

            LicenseApiResponse? api = null;
            try
            {
                api = JsonSerializer.Deserialize(text, LicenseJsonContext.Default.LicenseApiResponse);
            }
            catch
            {
                // non-JSON body (proxy page, 5xx html): treated as a network problem below
            }

            if (api == null)
                return Fail("network");

            if (api.Ok)
            {
                return new LicenseResult
                {
                    Ok = true,
                    Plan = api.Plan ?? "",
                    ExpiresAt = ParseIso(api.ExpiresAt),
                    ActivatedDevices = api.ActivatedDevices ?? 0,
                    MaxDevices = api.MaxDevices ?? 0
                };
            }

            var code = string.IsNullOrWhiteSpace(api.Error) ? "bad_request" : api.Error!;
            return Fail(code);
        }
        catch (Exception)
        {
            // HttpRequestException, TaskCanceledException (timeout), socket errors
            return Fail("network");
        }
    }

    private static LicenseResult Fail(string code) => new()
    {
        Ok = false,
        Error = code,
        Message = ErrorToMessage(code)
    };

    // ------------------------------------------------------------------
    // HWID
    // ------------------------------------------------------------------

    /// <summary>
    /// SHA-256 hex (lowercase) of MachineGuid + MachineName. Falls back to a random GUID
    /// persisted in the config when the registry cannot be read.
    /// </summary>
    public string GetHwid()
    {
        if (_hwid != null) return _hwid;

        lock (_lock)
        {
            if (_hwid != null) return _hwid;

            string? machineGuid = ReadMachineGuid();
            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                _hwid = Sha256Hex(machineGuid + "|" + Environment.MachineName);
                return _hwid;
            }

            // Fallback: random GUID stored in the config
            var config = ConfigManager.LoadConfig();
            if (!string.IsNullOrWhiteSpace(config.LicenseHwid) && config.LicenseHwid.Length == 64)
            {
                _hwid = config.LicenseHwid;
                return _hwid;
            }

            _hwid = Sha256Hex(Guid.NewGuid().ToString("N") + "|" + Environment.MachineName);
            config.LicenseHwid = _hwid;
            ConfigManager.SaveConfig(config);
            return _hwid;
        }
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static readonly Regex KeyRegex = new("^PB-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$", RegexOptions.Compiled);

    /// <summary>Uppercases and re-inserts dashes: accepts "pb-xxxx-...", "PBXXXX...", or just the 16 chars.</summary>
    public static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder();
        foreach (var ch in raw.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
        }
        var compact = sb.ToString();
        if (compact.Length == 16) compact = "PB" + compact;
        if (compact.Length == 18 && compact.StartsWith("PB", StringComparison.Ordinal))
        {
            var body = compact.Substring(2);
            return $"PB-{body.Substring(0, 4)}-{body.Substring(4, 4)}-{body.Substring(8, 4)}-{body.Substring(12, 4)}";
        }
        return raw.Trim().ToUpperInvariant();
    }

    public static bool IsValidKeyFormat(string key) => KeyRegex.IsMatch(key);

    public static string MaskKey(string? key)
    {
        var k = NormalizeKey(key);
        if (!IsValidKeyFormat(k)) return "";
        return "PB-****-****-****-" + k.Substring(k.Length - 4);
    }

    public static string PlanToDisplayName(string? plan) => plan switch
    {
        "month" => "Месяц",
        "3months" => "3 месяца",
        "lifetime" => "Навсегда",
        _ => string.IsNullOrWhiteSpace(plan) ? "" : plan
    };

    public static string ErrorToMessage(string? code) => code switch
    {
        "not_found" => "Ключ не найден",
        "expired" => "Срок лицензии истёк",
        "device_limit" => "Ключ уже используется на максимальном числе устройств",
        "revoked" => "Ключ отозван",
        "not_activated" => "Это устройство не привязано к ключу",
        "bad_request" => "Неверный формат ключа",
        "network" => "Нет связи с сервером лицензий",
        _ => "Ошибка сервера лицензий: " + code
    };

    private static DateTime? ParseIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.UtcDateTime;
        return null;
    }
}
