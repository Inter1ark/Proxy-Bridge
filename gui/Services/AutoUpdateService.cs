using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

// ---- Manifest DTOs (site/update/latest.json, source generated, see UpdateManifestJsonContext) ----

public class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("published")] public string? Published { get; set; }
    [JsonPropertyName("min_supported")] public string? MinSupported { get; set; }
    [JsonPropertyName("notes")] public UpdateNotes? Notes { get; set; }
    [JsonPropertyName("windows")] public UpdateWindowsAsset? Windows { get; set; }
    [JsonPropertyName("macos")] public UpdateMacAssets? MacOs { get; set; }
}

public class UpdateNotes
{
    [JsonPropertyName("ru")] public List<string>? Ru { get; set; }
    [JsonPropertyName("en")] public List<string>? En { get; set; }
}

public class UpdateWindowsAsset
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("fallback_url")] public string? FallbackUrl { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class UpdateMacAssets
{
    [JsonPropertyName("arm64")] public string? Arm64 { get; set; }
    [JsonPropertyName("x64")] public string? X64 { get; set; }
    [JsonPropertyName("page")] public string? Page { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal partial class UpdateManifestJsonContext : JsonSerializerContext
{
}

/// <summary>Result of a manifest check.</summary>
public class UpdateCheckResult
{
    public UpdateManifest Manifest { get; init; } = new();
    public Version Current { get; init; } = new(0, 0, 0, 0);
    public Version Latest { get; init; } = new(0, 0, 0, 0);
    public bool IsNewer => Latest > Current;
    /// <summary>Current version is below min_supported (only meaningful when a newer version exists).</summary>
    public bool IsMandatory { get; init; }
    public string LatestText => AutoUpdateService.FormatVersion(Latest);
}

/// <summary>Failure with a machine-readable code (network, server, hash_mismatch, download_failed, install_failed, unsupported_platform).</summary>
public class UpdateException : Exception
{
    public string Code { get; }
    public UpdateException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>
/// Auto-update logic without any UI dependency (UpdateViewModel drives it and marshals to the UI thread).
/// Flow: CheckAsync (manifest) -> DownloadAndVerifyAsync (url, then fallback_url, SHA-256) ->
/// LaunchInstallHelper (detached update.cmd waits for this PID to exit, runs the installer with /S /D=dir,
/// relaunches the app) -> the caller shuts the app down.
/// </summary>
public class AutoUpdateService
{
    public const string DefaultManifestUrl = "https://www.proxybridge.org/update/latest.json";
    public const string MacDownloadPage = "https://www.proxybridge.org/skachat/";

    /// <summary>
    /// Hook awaited right before the app exits to install an update. The GUI sets it to stop interception
    /// cleanly (disconnect the proxy engine, release WinDivert). Exceptions are swallowed and the call is
    /// limited to <see cref="BeforeInstallTimeout"/>, so a hanging hook cannot block the update.
    /// Example: AutoUpdateService.BeforeInstall = () => engine.StopAsync();
    /// </summary>
    public static Func<Task>? BeforeInstall { get; set; }

    public static readonly TimeSpan BeforeInstallTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);

    private readonly HttpClient _http;

    public string ManifestUrl { get; }
    public Version CurrentVersion { get; }
    /// <summary>%TEMP%\ProxyBridge-Update</summary>
    public string WorkDir { get; }

    public AutoUpdateService(string? manifestUrl = null, Version? currentVersion = null, string? workDir = null, HttpClient? http = null)
    {
        var env = Environment.GetEnvironmentVariable("PROXYBRIDGE_UPDATE_URL");
        ManifestUrl = manifestUrl ?? (string.IsNullOrWhiteSpace(env) ? DefaultManifestUrl : env.Trim());
        CurrentVersion = Normalize(currentVersion ?? GetEntryVersion());
        WorkDir = workDir ?? Path.Combine(Path.GetTempPath(), "ProxyBridge-Update");
        _http = http ?? SharedHttp.Value;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        // Overall timeout is enforced per request with a CancellationToken, the download can take minutes.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyBridge/" + FormatVersion(GetEntryVersion()));
        return client;
    }

    // ------------------------------------------------------------------
    // Versions
    // ------------------------------------------------------------------

    public static Version GetEntryVersion()
    {
        var v = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        return Normalize(v ?? new Version(0, 0, 0, 0));
    }

    /// <summary>Makes "3.3" and "3.3.0.0" compare equal (missing parts become 0).</summary>
    public static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0) s = s[..dash];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>"3.3.0" (the 4th part only when it is not zero).</summary>
    public static string FormatVersion(Version v) =>
        v.Revision > 0 ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{v.Revision}" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    /// <summary>Pure comparison logic (unit tested).</summary>
    public static UpdateCheckResult Evaluate(UpdateManifest manifest, Version current)
    {
        if (!TryParseVersion(manifest.Version, out var latest))
            throw new UpdateException("server", "Manifest has no valid version");
        current = Normalize(current);
        var mandatory = latest > current
                        && TryParseVersion(manifest.MinSupported, out var min)
                        && current < min;
        return new UpdateCheckResult { Manifest = manifest, Current = current, Latest = latest, IsMandatory = mandatory };
    }

    /// <summary>Whether the startup check should open the update window.</summary>
    public static bool ShouldPrompt(UpdateCheckResult result, bool autoCheck, string? skippedVersion)
    {
        if (!result.IsNewer) return false;
        if (result.IsMandatory) return true;
        if (!autoCheck) return false;
        if (TryParseVersion(skippedVersion, out var skipped) && skipped == result.Latest) return false;
        return true;
    }

    // ------------------------------------------------------------------
    // Manifest
    // ------------------------------------------------------------------

    /// <summary>Downloads and evaluates the manifest. Throws UpdateException("network" | "server").</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
        return Evaluate(manifest, CurrentVersion);
    }

    public async Task<UpdateManifest> FetchManifestAsync(CancellationToken ct = default)
    {
        string json;
        var uri = ToUri(ManifestUrl);
        if (uri.IsFile)
        {
            try { json = await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new UpdateException("network", "Cannot read manifest file: " + ex.Message, ex); }
        }
        else
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            HttpResponseMessage resp;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                throw new UpdateException("network", "Manifest request failed: " + ex.Message, ex);
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateException("server", $"Manifest HTTP {(int)resp.StatusCode}");
                json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(json.TrimStart('﻿'), UpdateManifestJsonContext.Default.UpdateManifest);
            if (manifest == null || !TryParseVersion(manifest.Version, out _))
                throw new UpdateException("server", "Manifest is empty or has no version");
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new UpdateException("server", "Manifest is not valid JSON: " + ex.Message, ex);
        }
    }

    private static Uri ToUri(string urlOrPath)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
            return uri;
        // plain local path
        return new Uri(Path.GetFullPath(urlOrPath));
    }

    // ------------------------------------------------------------------
    // Download + verify
    // ------------------------------------------------------------------

    public string InstallerPathFor(string version) =>
        Path.Combine(WorkDir, $"ProxyBridge-Setup-{version}.exe");

    /// <summary>
    /// Downloads the Windows installer (url, then fallback_url) to WorkDir and verifies SHA-256.
    /// progress receives (bytesReceived, bytesTotal; total is 0 when unknown); onVerifying is called before hashing.
    /// Throws UpdateException: hash_mismatch (file deleted), download_failed, server (no Windows asset).
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateManifest manifest,
        IProgress<(long received, long total)>? progress = null,
        Action? onVerifying = null,
        CancellationToken ct = default)
    {
        var win = manifest.Windows;
        var expected = NormalizeHash(win?.Sha256);
        if (win == null || (string.IsNullOrWhiteSpace(win.Url) && string.IsNullOrWhiteSpace(win.FallbackUrl)))
            throw new UpdateException("server", "Manifest has no Windows installer");
        if (expected.Length != 64)
            throw new UpdateException("server", "Manifest has no valid sha256");

        Directory.CreateDirectory(WorkDir);
        TryParseVersion(manifest.Version, out var ver);
        var target = InstallerPathFor(FormatVersion(ver));

        // Already downloaded earlier and intact: reuse it.
        if (File.Exists(target))
        {
            onVerifying?.Invoke();
            if (string.Equals(await ComputeSha256Async(target, ct).ConfigureAwait(false), expected, StringComparison.Ordinal))
            {
                var len = new FileInfo(target).Length;
                progress?.Report((len, len));
                return target;
            }
            TryDelete(target);
        }

        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(win.Url)) urls.Add(win.Url.Trim());
        if (!string.IsNullOrWhiteSpace(win.FallbackUrl) && !urls.Contains(win.FallbackUrl.Trim())) urls.Add(win.FallbackUrl.Trim());

        var sawHashMismatch = false;
        Exception? lastError = null;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            var part = target + ".part";
            try
            {
                await DownloadFileAsync(url, part, win.Size, progress, ct).ConfigureAwait(false);
                onVerifying?.Invoke();
                var actual = await ComputeSha256Async(part, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    sawHashMismatch = true;
                    lastError = new UpdateException("hash_mismatch", $"SHA-256 mismatch for {url}: {actual}");
                    TryDelete(part);
                    continue;
                }
                File.Move(part, target, overwrite: true);
                return target;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(part);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(part);
            }
        }

        if (sawHashMismatch)
            throw new UpdateException("hash_mismatch", lastError?.Message ?? "SHA-256 mismatch");
        throw new UpdateException("download_failed", "Download failed: " + lastError?.Message, lastError);
    }

    private async Task DownloadFileAsync(string url, string dest, long expectedSize,
        IProgress<(long, long)>? progress, CancellationToken ct)
    {
        var uri = ToUri(url);
        Stream source;
        long total = expectedSize > 0 ? expectedSize : 0;
        HttpResponseMessage? resp = null;
        if (uri.IsFile)
        {
            source = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (total == 0) total = source.Length;
        }
        else
        {
            resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                resp.Dispose();
                throw new HttpRequestException($"HTTP {code} for {url}");
            }
            if (resp.Content.Headers.ContentLength is long cl && cl > 0) total = cl;
            source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            progress?.Report((0, total));
            var sw = Stopwatch.StartNew();
            while (true)
            {
                // Stall guard: no data for 60 s aborts this URL (the fallback is tried next).
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try { n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new IOException("Download stalled: " + url); }
                if (n == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (sw.ElapsedMilliseconds >= 100)
                {
                    progress?.Report((received, total));
                    sw.Restart();
                }
            }
            progress?.Report((received, total > 0 ? total : received));
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            resp?.Dispose();
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeHash(string? h) => (h ?? "").Trim().ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------
    // Install
    // ------------------------------------------------------------------

    /// <summary>Awaits <see cref="BeforeInstall"/> with a timeout. Never throws.</summary>
    public static async Task RunBeforeInstallAsync()
    {
        var hook = BeforeInstall;
        if (hook == null) return;
        try
        {
            var task = hook();
            await Task.WhenAny(task, Task.Delay(BeforeInstallTimeout)).ConfigureAwait(false);
        }
        catch
        {
            // the update must go on even if the clean stop failed; the helper also stops WinDivert
        }
    }

    /// <summary>Install directory of the running app, without a trailing backslash.</summary>
    public static string CurrentInstallDir() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    public static string CurrentExeName() =>
        Path.GetFileName(Environment.ProcessPath ?? "ProxyBridge.exe") is { Length: > 0 } n ? n : "ProxyBridge.exe";

    /// <summary>
    /// The helper script. It is pure ASCII: every path comes from environment variables
    /// (PB_PID, PB_INSTALLER, PB_INSTDIR, PB_APP, PB_LOG) set on the cmd.exe process, expanded with delayed
    /// expansion, so Unicode, spaces, %, &amp; and parentheses in paths are safe.
    /// NSIS needs /D= as the last argument and unquoted; it reads the rest of the command line, spaces included.
    /// </summary>
    public static string BuildUpdateScript()
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L("@echo off");
        L("rem ProxyBridge update helper (generated by the app). Inputs: PB_PID PB_INSTALLER PB_INSTDIR PB_APP PB_LOG");
        L("setlocal EnableExtensions EnableDelayedExpansion");
        L(">>\"!PB_LOG!\" echo [%date% %time%] helper started, waiting for PID %PB_PID% to exit");
        L(">>\"!PB_LOG!\" echo installer=\"!PB_INSTALLER!\"");
        L(">>\"!PB_LOG!\" echo instdir=\"!PB_INSTDIR!\"");
        L(">>\"!PB_LOG!\" echo app=\"!PB_APP!\"");
        L("rem System tools by full path: a find.exe or timeout.exe from Git or MSYS on PATH must not shadow them.");
        L("set \"PB_SYS=%SystemRoot%\\System32\"");
        L("set /a PB_WAITED=0");
        L(":waitloop");
        L("rem CSV row of the process is \"image\",\"pid\",...; the no-match message has no such field.");
        L("set PB_ALIVE=");
        L("for /f \"tokens=2 delims=,\" %%a in ('%SystemRoot%\\System32\\tasklist.exe /FI \"PID eq %PB_PID%\" /FO CSV /NH 2^>nul') do if \"%%~a\"==\"%PB_PID%\" set PB_ALIVE=1");
        L("if not defined PB_ALIVE goto exited");
        L("set /a PB_WAITED+=1");
        L("if !PB_WAITED! GEQ 120 (");
        L("  >>\"!PB_LOG!\" echo [%date% %time%] PID %PB_PID% still running after 120 s, killing it");
        L("  \"!PB_SYS!\\taskkill.exe\" /F /PID %PB_PID% >nul 2>&1");
        L(")");
        L("\"!PB_SYS!\\timeout.exe\" /t 1 /nobreak >nul 2>&1");
        L("if errorlevel 1 \"!PB_SYS!\\PING.EXE\" -n 2 127.0.0.1 >nul 2>&1");
        L("goto waitloop");
        L(":exited");
        L(">>\"!PB_LOG!\" echo [%date% %time%] app exited after !PB_WAITED! s, stopping WinDivert");
        L("\"!PB_SYS!\\sc.exe\" stop WinDivert >nul 2>&1");
        L("\"!PB_SYS!\\PING.EXE\" -n 2 127.0.0.1 >nul 2>&1");
        L(">>\"!PB_LOG!\" echo [%date% %time%] running installer silently");
        L("\"!PB_INSTALLER!\" /S /D=!PB_INSTDIR!");
        L("set PB_RC=!errorlevel!");
        L(">>\"!PB_LOG!\" echo [%date% %time%] installer exit code !PB_RC!");
        L("cd /d \"!PB_INSTDIR!\"");
        L(">>\"!PB_LOG!\" echo [%date% %time%] relaunching app");
        L("start \"\" \"!PB_APP!\"");
        L("if errorlevel 1 >>\"!PB_LOG!\" echo [%date% %time%] relaunch failed");
        L("if \"!PB_RC!\"==\"0\" (");
        L("  del /f /q \"!PB_INSTALLER!\" >nul 2>&1");
        L("  >>\"!PB_LOG!\" echo [%date% %time%] installer deleted");
        L(")");
        L(">>\"!PB_LOG!\" echo [%date% %time%] done");
        L("endlocal");
        L("exit /b 0");
        return sb.ToString();
    }

    /// <summary>Builds the start info for the helper (exposed for tests).</summary>
    public ProcessStartInfo BuildHelperStartInfo(string scriptPath, int pid, string installerPath, string installDir, string appPath, string logPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // /c ""path"": outer quotes are stripped by cmd, inner ones keep a path with spaces intact.
            Arguments = "/d /c \"\"" + scriptPath + "\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir,
        };
        psi.Environment["PB_PID"] = pid.ToString(CultureInfo.InvariantCulture);
        psi.Environment["PB_INSTALLER"] = installerPath;
        psi.Environment["PB_INSTDIR"] = Path.TrimEndingDirectorySeparator(installDir);
        psi.Environment["PB_APP"] = appPath;
        psi.Environment["PB_LOG"] = logPath;
        return psi;
    }

    /// <summary>
    /// Writes update.cmd and starts it hidden. The helper inherits this process's elevation (the app runs as
    /// administrator), so neither the installer nor the relaunched app shows a UAC prompt.
    /// Throws UpdateException("install_failed") when the helper cannot be started.
    /// </summary>
    public void LaunchInstallHelper(string installerPath, int? pid = null, string? installDir = null, string? appPath = null)
    {
        try
        {
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("Installer not found", installerPath);
            Directory.CreateDirectory(WorkDir);
            var dir = installDir ?? CurrentInstallDir();
            var app = appPath ?? Path.Combine(dir, CurrentExeName());
            var script = Path.Combine(WorkDir, "update.cmd");
            var log = Path.Combine(WorkDir, "update.log");
            File.WriteAllText(script, BuildUpdateScript(), Encoding.ASCII);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} app {FormatVersion(CurrentVersion)} starting update helper{Environment.NewLine}");
            var psi = BuildHelperStartInfo(script, pid ?? Environment.ProcessId, installerPath, dir, app, log);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException("install_failed", "Cannot start the update helper: " + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------
    // macOS
    // ------------------------------------------------------------------

    public static string MacPageUrl(UpdateManifest? manifest) =>
        string.IsNullOrWhiteSpace(manifest?.MacOs?.Page) ? MacDownloadPage : manifest!.MacOs!.Page!;

    /// <summary>Opens a URL in the default browser. Returns false on failure.</summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
