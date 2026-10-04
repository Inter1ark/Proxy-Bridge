using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// macOS engine: launches the bundled pbcore helper as root (one administrator
/// password prompt per connect), then talks to it over a token-protected
/// localhost control socket to receive status/stats lines and to stop it.
/// </summary>
public sealed class MacProxyEngine : IProxyEngine
{
    private const int ControlPort = 34050;
    private const string DefaultDns = "1.1.1.1";

    private readonly string _dataDir;
    private readonly string _tokenFile;
    private readonly string _logFile;

    private string _proxyType = "SOCKS5";
    private string _proxyIp = "";
    private ushort _proxyPort;
    private string _proxyUser = "";
    private string _proxyPass = "";

    private string _token = "";
    private TcpClient? _watch;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private volatile bool _isRunning;
    private volatile bool _stopRequested;
    private uint _nextRuleId = 1;
    private long _up;
    private long _down;
    private string _lastError = "";

    public MacProxyEngine()
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProxyBridge");
        _tokenFile = Path.Combine(_dataDir, "pbcore.token");
        _logFile = Path.Combine(_dataDir, "pbcore.log");
        try { Directory.CreateDirectory(_dataDir); } catch { }
    }

    public bool SupportsSplitTunnel => false;
    public bool ProvidesTrafficStats => true;
    public string LastError => _lastError;
    public bool IsRunning => _isRunning;

    public event Action<string>? LogReceived;
    public event Action<string>? Stopped;

    private void Log(string msg) => LogReceived?.Invoke(msg);

    public bool SetProxyConfig(string type, string ip, ushort port, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(ip) || port == 0) return false;
        _proxyType = type.ToUpperInvariant() == "HTTP" ? "HTTP" : "SOCKS5";
        _proxyIp = ip.Trim();
        _proxyPort = port;
        _proxyUser = username ?? "";
        _proxyPass = password ?? "";
        return true;
    }

    // pbcore is TCP-only and always forwards DNS directly; these are accepted for API parity.
    public void SetDisableUdp(bool disable) { }
    public void SetDnsViaProxy(bool enable) { }

    private string BuildProxyUrl()
    {
        var scheme = _proxyType == "HTTP" ? "http" : "socks5";
        var auth = "";
        if (_proxyUser.Length > 0 || _proxyPass.Length > 0)
            auth = Uri.EscapeDataString(_proxyUser) + ":" + Uri.EscapeDataString(_proxyPass) + "@";
        return $"{scheme}://{auth}{_proxyIp}:{_proxyPort}";
    }

    private static string PbcorePath => Path.Combine(AppContext.BaseDirectory, "pbcore");

    public async Task<bool> StartAsync()
    {
        _lastError = "";
        if (_isRunning) return true;
        if (string.IsNullOrEmpty(_proxyIp))
        {
            _lastError = "Proxy is not configured";
            return false;
        }
        if (!File.Exists(PbcorePath))
        {
            _lastError = $"pbcore helper not found at {PbcorePath}";
            Log(_lastError);
            return false;
        }

        // A helper left over from a previous session (for example after a crash) would hold the port.
        await StopStaleInstanceAsync();

        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try { File.WriteAllText(_tokenFile, _token); SetPrivate(_tokenFile); } catch { }

        var proxyFile = Path.Combine(_dataDir, "pbcore.proxy");
        var launcher = Path.Combine(_dataDir, "pbcore-launch.sh");
        try
        {
            File.WriteAllText(proxyFile, BuildProxyUrl() + "\n");
            SetPrivate(proxyFile);

            var args = string.Join(" ",
                Q(PbcorePath),
                "--proxy-file", Q(proxyFile),
                "--dns", DefaultDns,
                "--control-port", ControlPort.ToString(),
                "--control-token", _token,
                "--parent-pid", Environment.ProcessId.ToString(),
                "--log-level", "info");

            var script = new StringBuilder();
            script.Append("#!/bin/sh\n");
            script.Append("chmod 755 ").Append(Q(PbcorePath)).Append(" 2>/dev/null\n");
            script.Append("xattr -d com.apple.quarantine ").Append(Q(PbcorePath)).Append(" 2>/dev/null\n");
            script.Append("nohup ").Append(args).Append(" > ").Append(Q(_logFile)).Append(" 2>&1 < /dev/null &\n");
            script.Append("exit 0\n");
            File.WriteAllText(launcher, script.ToString());
            SetPrivate(launcher);
        }
        catch (Exception ex)
        {
            _lastError = $"Cannot write launcher: {ex.Message}";
            Log(_lastError);
            return false;
        }

        _stopRequested = false;
        _up = 0;
        _down = 0;
        Log("Requesting administrator privileges to start pbcore...");
        var (ok, output) = await RunOsascriptAsync(launcher);
        try { File.Delete(launcher); } catch { }
        if (!ok)
        {
            _lastError = output.Contains("-128") || output.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                ? "Administrator password prompt was cancelled"
                : $"Could not start pbcore: {output}";
            Log(_lastError);
            try { File.Delete(proxyFile); } catch { }
            return false;
        }

        // The helper opens its control socket before touching the network, so connect first
        // and then wait for the "up" line.
        var client = await ConnectControlAsync(TimeSpan.FromSeconds(8));
        if (client == null)
        {
            _lastError = "pbcore did not start (control socket unreachable). " + TailLog();
            Log(_lastError);
            try { File.Delete(proxyFile); } catch { }
            return false;
        }

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"watch {_token}\n"));
        var reader = new StreamReader(stream, Encoding.UTF8);

        var upTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch = client;
        _watchCts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchLoop(reader, upTcs, _watchCts.Token));

        var first = await Task.WhenAny(upTcs.Task, Task.Delay(TimeSpan.FromSeconds(45)));
        try { File.Delete(proxyFile); } catch { }
        if (first != upTcs.Task || !upTcs.Task.Result)
        {
            if (string.IsNullOrEmpty(_lastError)) _lastError = "pbcore did not report ready in time. " + TailLog();
            Log(_lastError);
            Stop();
            return false;
        }
        _isRunning = true;
        return true;
    }

    private void WatchLoop(StreamReader reader, TaskCompletionSource<bool> upTcs, CancellationToken ct)
    {
        string? exitMessage = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                if (line.Length == 0) continue;
                if (!line.StartsWith('{'))
                {
                    Log($"[pbcore] {line}");
                    continue;
                }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { Log($"[pbcore] {line}"); continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    switch (status)
                    {
                        case "up":
                            Log($"[pbcore] up: tun={GetStr(root, "tun")} proxy={GetStr(root, "proxy")}");
                            upTcs.TrySetResult(true);
                            break;
                        case "stats":
                            if (root.TryGetProperty("up", out var u) && u.TryGetInt64(out var upv)) Interlocked.Exchange(ref _up, upv);
                            if (root.TryGetProperty("down", out var d) && d.TryGetInt64(out var dnv)) Interlocked.Exchange(ref _down, dnv);
                            break;
                        case "error":
                            var msg = GetStr(root, "message");
                            Log($"[pbcore] error: {msg}");
                            _lastError = msg;
                            exitMessage = msg;
                            upTcs.TrySetResult(false);
                            break;
                        case "down":
                            Log("[pbcore] down");
                            exitMessage ??= "pbcore stopped";
                            upTcs.TrySetResult(false);
                            break;
                        default:
                            Log($"[pbcore] {line}");
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) exitMessage ??= $"control connection lost: {ex.Message}";
        }
        finally
        {
            upTcs.TrySetResult(false);
            var wasRunning = _isRunning;
            _isRunning = false;
            if (wasRunning && !_stopRequested)
            {
                Stopped?.Invoke(exitMessage ?? "pbcore exited unexpectedly");
            }
        }
    }

    private static string GetStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public bool Stop()
    {
        _stopRequested = true;
        var hadWatch = _watch != null;
        try
        {
            if (!string.IsNullOrEmpty(_token))
            {
                var reply = SendCommand("stop", _token, TimeSpan.FromSeconds(2));
                if (reply != null) Log($"[pbcore] stop -> {reply}");
            }
        }
        catch (Exception ex)
        {
            Log($"[pbcore] stop failed: {ex.Message}");
        }

        try
        {
            // Give the helper a moment to roll back routes; the watch stream ends when it exits.
            _watchTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        try { _watchCts?.Cancel(); } catch { }
        try { _watch?.Close(); } catch { }
        _watch = null;
        _watchCts = null;
        _watchTask = null;
        _isRunning = false;
        if (hadWatch) Log("[pbcore] stopped");
        return true;
    }

    private async Task StopStaleInstanceAsync()
    {
        string? oldToken = null;
        try { if (File.Exists(_tokenFile)) oldToken = File.ReadAllText(_tokenFile).Trim(); } catch { }
        if (string.IsNullOrEmpty(oldToken)) return;
        string? reply = null;
        try { reply = SendCommand("stop", oldToken, TimeSpan.FromSeconds(1)); } catch { }
        if (reply != null)
        {
            Log("[pbcore] stopped a previous instance");
            await Task.Delay(1500);
        }
    }

    private static string? SendCommand(string command, string token, TimeSpan timeout)
    {
        using var client = new TcpClient();
        var connect = client.ConnectAsync("127.0.0.1", ControlPort);
        if (!connect.Wait(timeout)) return null;
        using var stream = client.GetStream();
        stream.ReadTimeout = (int)timeout.TotalMilliseconds;
        stream.WriteTimeout = (int)timeout.TotalMilliseconds;
        var data = Encoding.ASCII.GetBytes($"{command} {token}\n");
        stream.Write(data, 0, data.Length);
        var buf = new byte[256];
        var n = stream.Read(buf, 0, buf.Length);
        return n > 0 ? Encoding.ASCII.GetString(buf, 0, n).Trim() : "";
    }

    private static async Task<TcpClient?> ConnectControlAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync("127.0.0.1", ControlPort);
                var done = await Task.WhenAny(connect, Task.Delay(500));
                if (done == connect && client.Connected)
                {
                    await connect;
                    return client;
                }
            }
            catch { }
            client.Dispose();
            await Task.Delay(250);
        }
        return null;
    }

    private static async Task<(bool ok, string output)> RunOsascriptAsync(string scriptPath)
    {
        // AppleScript: do shell script "/bin/sh " & quoted form of "<path>" with administrator privileges
        var escaped = scriptPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var apple = $"do shell script \"/bin/sh \" & quoted form of \"{escaped}\" with administrator privileges";
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(apple);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "osascript could not be started");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var output = (await stdout + "\n" + await stderr).Trim();
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string TailLog()
    {
        try
        {
            if (!File.Exists(_logFile)) return "";
            var lines = File.ReadAllLines(_logFile);
            var start = Math.Max(0, lines.Length - 5);
            return "Log: " + string.Join(" | ", lines, start, lines.Length - start);
        }
        catch
        {
            return "";
        }
    }

    private static void SetPrivate(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { }
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // Rules are not needed by pbcore (everything except local networks goes through the
    // proxy by design); ids are returned so callers can keep their bookkeeping.
    public uint AddRule(string processName, string targetHosts, string targetPorts, string protocol, string action)
        => _nextRuleId++;

    public bool DeleteRule(uint ruleId) => true;

    public uint AddProxy(string type, string ip, ushort port, string username, string password) => 0;

    public void ClearProxies() { }

    public uint AddRuleEx(string processName, string targetHosts, string targetPorts, string protocol, string action, uint proxyId) => 0;

    public bool TryGetTrafficStats(out long upBytes, out long downBytes)
    {
        upBytes = Interlocked.Read(ref _up);
        downBytes = Interlocked.Read(ref _down);
        return _isRunning;
    }

    public void Dispose()
    {
        if (_isRunning || _watch != null)
        {
            try { Stop(); } catch { }
        }
    }
}
