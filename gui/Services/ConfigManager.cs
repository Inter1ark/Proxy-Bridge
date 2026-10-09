using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Services;

public class AppConfig
{
    public string ProxyType { get; set; } = "SOCKS5";
    public string ProxyIp { get; set; } = "";
    public string ProxyPort { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public bool DnsViaProxy { get; set; } = true;
    /// <summary>"ru", "en", or "" for auto (system UI culture) on first run.</summary>
    public string Language { get; set; } = "";
    /// <summary>
    /// True once the user picked a language in the app. Configs written by older versions carry
    /// "Language": "en" as a default value, so without this flag the language is auto-detected.
    /// </summary>
    public bool LanguageChosen { get; set; }
    /// <summary>Legacy (before rules v2): "system" or "apps". Read once for migration, no longer written.</summary>
    public string ConnectionMode { get; set; } = "system";
    /// <summary>
    /// 2 = rules table model: <see cref="ProxyMappings"/> are app rows (ProxyString "" = direct) and
    /// <see cref="DefaultProxy"/> is the "all other apps" target. 0 = an older config that still needs migration.
    /// </summary>
    public int RulesVersion { get; set; }
    /// <summary>Proxy for "all other apps" (a saved proxy string) or "" for a direct connection.</summary>
    public string DefaultProxy { get; set; } = "";
    /// <summary>Last check result per proxy string.</summary>
    public Dictionary<string, ProxyCheckInfo> ProxyChecks { get; set; } = new();
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool AutoConnectLastProxy { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    public bool DisableUdp { get; set; } = true;
    public string LastProxyInput { get; set; } = "";
    public List<string> ProxyHistory { get; set; } = new();
    public List<string> LoadedProxyList { get; set; } = new();
    public List<ProxyRuleConfig> ProxyRules { get; set; } = new();
    // "Branching" mode: per-program proxy assignments
    public List<AppProxyMapping> ProxyMappings { get; set; } = new();
    /// <summary>Display names of proxies: proxy string -> label ("Amsterdam", "Frankfurt, DE"). Filled after a successful check or by renaming.</summary>
    public Dictionary<string, string> ProxyLabels { get; set; } = new();

    // License cache (see Services/LicenseService.cs and docs/LICENSE_API.md)
    public string LicenseKey { get; set; } = "";
    public string LicensePlan { get; set; } = "";
    /// <summary>ISO 8601 UTC or empty for lifetime.</summary>
    public string LicenseExpiresAt { get; set; } = "";
    /// <summary>ISO 8601 UTC time of the last successful server check.</summary>
    public string LicenseLastCheckUtc { get; set; } = "";
    /// <summary>SHA-256 hex hardware id (64 chars).</summary>
    public string LicenseHwid { get; set; } = "";
}

/// <summary>Per-application proxy assignment used by Split Tunnel (Apps page).</summary>
public class AppProxyMapping : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;

    public string ProcessName { get; set; } = "";   // e.g. "chrome.exe"
    /// <summary>A saved proxy string ("socks5://user:pass@ip:port"), or "" for a direct connection.</summary>
    public string ProxyString { get; set; } = "";
    /// <summary>Full path of the program when it was picked with "Browse" (used for its icon), or empty.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>A disabled route is kept in the list but no Split Tunnel rule is created for it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Proxy without the password, for lists: "SOCKS5  1.2.3.4:1080  user:••••".</summary>
    [JsonIgnore]
    public string ProxyMasked => ProxyParser.Describe(ProxyString);

    [JsonIgnore]
    public string ProxyType => ProxyParser.TryParse(ProxyString, out var p) ? p.Type : "";

    [JsonIgnore]
    public string ProxyHostPort => ProxyParser.TryParse(ProxyString, out var p) ? p.HostPort : ProxyParser.Mask(ProxyString);

    [JsonIgnore]
    public string ProxyAuth => ProxyParser.TryParse(ProxyString, out var p) ? p.MaskedAuth : "";
}

/// <summary>Stored result of the last proxy check (see Services/ProxyChecker.cs).</summary>
public class ProxyCheckInfo
{
    public bool Ok { get; set; }
    /// <summary>"" when Ok, else auth_failed, unreachable, timeout or bad_response.</summary>
    public string Error { get; set; } = "";
    public string Ip { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public int LatencyMs { get; set; }
    /// <summary>ISO 8601 UTC.</summary>
    public string CheckedAt { get; set; } = "";
}

public class ProxyRuleConfig
{
    public string ProcessName { get; set; } = "";
    public string TargetHosts { get; set; } = "*";
    public string TargetPorts { get; set; } = "*";
    public string Protocol { get; set; } = "TCP";
    public string Action { get; set; } = "PROXY";
    public bool IsEnabled { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ProxyRuleConfig))]
[JsonSerializable(typeof(List<ProxyRuleConfig>))]
[JsonSerializable(typeof(AppProxyMapping))]
[JsonSerializable(typeof(List<AppProxyMapping>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ProxyCheckInfo))]
[JsonSerializable(typeof(Dictionary<string, ProxyCheckInfo>))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

public static class ConfigManager
{
    /// <summary>
    /// Data directory: %APPDATA%\ProxyBridge (on macOS the .NET ApplicationData folder), or the
    /// PROXYBRIDGE_CONFIG_DIR environment variable when it is set (testing without touching real data).
    /// Every component that stores files must use this directory.
    /// </summary>
    public static readonly string ConfigDirectory = ResolveConfigDirectory();

    private static string ResolveConfigDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("PROXYBRIDGE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir.Trim();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProxyBridge");
    }

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");

    public static bool SaveConfig(AppConfig config)
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }

            var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            // write to a temp file first so a crash never leaves a half-written config
            var tmp = ConfigFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config save failed: {ex.Message}");
            return false;
        }
    }

    public static AppConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
            return config ?? new AppConfig();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config load failed: {ex.Message}");
            return new AppConfig();
        }
    }

    public static bool ConfigExists()
    {
        return File.Exists(ConfigFilePath);
    }
}
