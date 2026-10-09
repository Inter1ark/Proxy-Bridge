using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyBridge.GUI.Services;

/// <summary>Auto-update preferences, stored in &lt;configDir&gt;\update.json.</summary>
public class UpdatePrefs
{
    [JsonPropertyName("auto_check")] public bool AutoCheck { get; set; } = true;
    [JsonPropertyName("skipped_version")] public string SkippedVersion { get; set; } = "";
    [JsonPropertyName("last_check_utc")] public DateTime? LastCheckUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdatePrefs))]
internal partial class UpdatePrefsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads and saves <see cref="UpdatePrefs"/>. Config dir: env PROXYBRIDGE_CONFIG_DIR when set,
/// otherwise %APPDATA%\ProxyBridge. Never throws.
/// </summary>
public class UpdateStore
{
    private readonly object _lock = new();

    public string FilePath { get; }

    public UpdateStore(string? configDir = null)
    {
        FilePath = Path.Combine(configDir ?? DefaultConfigDir(), "update.json");
    }

    public static string DefaultConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("PROXYBRIDGE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProxyBridge");
    }

    public UpdatePrefs Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new UpdatePrefs();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize(json, UpdatePrefsJsonContext.Default.UpdatePrefs) ?? new UpdatePrefs();
            }
            catch
            {
                return new UpdatePrefs();
            }
        }
    }

    public bool Save(UpdatePrefs prefs)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, UpdatePrefsJsonContext.Default.UpdatePrefs));
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Load, change, save in one step.</summary>
    public UpdatePrefs Update(Action<UpdatePrefs> change)
    {
        lock (_lock)
        {
            var prefs = Load();
            change(prefs);
            Save(prefs);
            return prefs;
        }
    }
}
