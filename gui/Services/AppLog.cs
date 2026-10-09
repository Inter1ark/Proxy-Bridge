using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// Support log: core log callback, connection steps and app errors, one file per day
/// (proxybridge-YYYYMMDD.log), kept for 7 days. Folder: %LOCALAPPDATA%\ProxyBridge\logs, or
/// &lt;PROXYBRIDGE_CONFIG_DIR&gt;\logs when that variable is set (tests). Every line goes through
/// <see cref="Sanitize"/>, so proxy credentials are never written.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 7;
    private static readonly object Gate = new();
    private static bool _cleaned;

    public static readonly string Directory = ResolveDirectory();

    private static string ResolveDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("PROXYBRIDGE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.Combine(overrideDir.Trim(), "logs");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProxyBridge", "logs");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");
    /// <summary>Lines from the native core log callback.</summary>
    public static void Core(string message) => Write("CORE", message);

    public static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Sanitize(message)}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (!_cleaned)
                {
                    _cleaned = true;
                    CleanOld();
                }
                var file = Path.Combine(Directory, $"proxybridge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break the app
        }
    }

    /// <summary>Deletes log files older than <see cref="KeepDays"/> days.</summary>
    private static void CleanOld()
    {
        try
        {
            var limit = DateTime.Today.AddDays(-(KeepDays - 1));
            foreach (var f in System.IO.Directory.GetFiles(Directory, "proxybridge-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(f).Substring("proxybridge-".Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < limit)
                {
                    try { File.Delete(f); } catch { /* in use */ }
                }
            }
        }
        catch
        {
        }
    }

    // scheme://user:pass@host or scheme://user@host
    private static readonly Regex UrlCreds = new(@"(?i)\b(socks5h?|socks4a?|https?)://[^\s/@]+@", RegexOptions.Compiled);
    // host:port:user:pass (user and pass without spaces; pass may contain ':')
    private static readonly Regex ColonCreds = new(
        @"(?i)\b((?:\d{1,3}(?:\.\d{1,3}){3})|(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+)):(\d{1,5}):[^\s:]+:\S+", RegexOptions.Compiled);
    // host:port:pass
    private static readonly Regex ColonPass = new(
        @"(?i)\b((?:\d{1,3}(?:\.\d{1,3}){3})|(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+)):(\d{1,5}):[^\s:]+", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"(?i)\b(pass(?:word)?|pwd|user(?:name)?)\s*[=:]\s*[^\s,;]+", RegexOptions.Compiled);

    /// <summary>Masks credentials in any proxy string inside <paramref name="message"/>.</summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var s = UrlCreds.Replace(message, m => m.Groups[1].Value + "://***:***@");
        s = ColonCreds.Replace(s, m => $"{m.Groups[1].Value}:{m.Groups[2].Value}:***:***");
        s = ColonPass.Replace(s, m => $"{m.Groups[1].Value}:{m.Groups[2].Value}:***");
        s = KeyValue.Replace(s, m => m.Groups[1].Value + "=***");
        return s;
    }
}
