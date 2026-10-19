using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// macOS engine. Preferred path: the pbcore daemon installed once as a launchd
/// job (<see cref="MacHelper"/>) takes "start"/"stop" over the control socket,
/// so connecting needs no password. Fallback: pbcore is launched as root for
/// one session (an administrator prompt per connect). Either way the engine
/// talks to pbcore over the token-protected localhost control socket to
/// receive status/stats lines and to replace the per-program rules while running.
///
/// Proxies and rules added through <see cref="IProxyEngine"/> are recorded here and
/// handed to pbcore as one JSON rule set: in a private file at start, or over the
/// control socket by <see cref="CommitRulesAsync"/> while running.
/// </summary>
public sealed class MacProxyEngine : IProxyEngine, ILiveRules
{
    private const int ControlPort = 34050;
    private const string DefaultDns = "1.1.1.1";

    private readonly string _dataDir;
    private readonly string _tokenFile;
    private readonly string _logFile;

    // Recorded table (see docs/CORE_API.md for the semantics shared with the Windows core)
    private readonly object _tableLock = new();
    private readonly List<(uint Id, string Url)> _proxies = new();
    private readonly List<(uint Id, string Process, string Action, uint ProxyId)> _rules = new();
    private uint _nextProxyId = 1;
    private uint _nextRuleId = 1;

    private string _token = "";
    private bool _viaDaemon;
    private TcpClient? _watch;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private volatile bool _isRunning;
    private volatile bool _stopRequested;
    private long _up;
    private long _down;
    private string _lastError = "";

    public MacProxyEngine()
    {
        _dataDir = ConfigManager.ConfigDirectory;
        _tokenFile = Path.Combine(_dataDir, "pbcore.token");
        _logFile = Path.Combine(_dataDir, "pbcore.log");
        try { Directory.CreateDirectory(_dataDir); } catch { }
    }

    public bool SupportsSplitTunnel => true;
    public bool ProvidesTrafficStats => true;
    public string LastError => _lastError;
    public bool IsRunning => _isRunning;

    public event Action<string>? LogReceived;
    public event Action<string>? Stopped;

    private void Log(string msg) => LogReceived?.Invoke(msg);

    // ------------------------------------------------------------------
    // Proxies and rules (recorded; applied at start or by CommitRulesAsync)
    // ------------------------------------------------------------------

    /// <summary>Legacy single-proxy setup: becomes proxy 1 with a catch-all rule.</summary>
    public bool SetProxyConfig(string type, string ip, ushort port, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(ip) || port == 0) return false;
        ClearProxies();
        var id = AddProxy(type, ip, port, username, password);
        if (id == 0) return false;
        AddRuleEx("*", "*", "*", "TCP", "PROXY", id);
        return true;
    }

    private string _udpMode = "direct";
    private bool _dnsViaProxy;
    private uint _dnsProxy;

    // Recorded and sent to pbcore with the rule set ("udp", "dns_via_proxy", "dns_proxy").
    public void SetDisableUdp(bool disable) => _udpMode = disable ? "block" : "direct";
    public void SetDnsViaProxy(bool enable) => _dnsViaProxy = enable;
    public void SetUdpMode(string mode) => _udpMode = mode;
    public void SetDnsProxy(uint proxyId) => _dnsProxy = proxyId;

    public uint AddProxy(string type, string ip, ushort port, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(ip) || port == 0) return 0;
        var scheme = (type ?? "").ToUpperInvariant() == "HTTP" ? "http" : "socks5";
        var auth = "";
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            auth = Uri.EscapeDataString(username ?? "") + ":" + Uri.EscapeDataString(password ?? "") + "@";
        var url = $"{scheme}://{auth}{ip.Trim()}:{port}";
        lock (_tableLock)
        {
            var id = _nextProxyId++;
            _proxies.Add((id, url));
            return id;
        }
    }

    public void ClearProxies()
    {
        lock (_tableLock)
        {
            _proxies.Clear();
            _rules.Clear();
        }
    }

    public uint AddRuleEx(string processName, string targetHosts, string targetPorts, string protocol, string action, uint proxyId)
    {
        var process = (processName ?? "").Trim();
        if (process.Length == 0) return 0;
        var act = (action ?? "").ToUpperInvariant() switch
        {
            "PROXY" => "proxy",
            "BLOCK" => "block",
            _ => "direct"
        };
        lock (_tableLock)
        {
            if (act == "proxy" && !_proxies.Any(p => p.Id == proxyId)) return 0;
            var id = _nextRuleId++;
            _rules.Add((id, process, act, act == "proxy" ? proxyId : 0));
            return id;
        }
    }

    public uint AddRule(string processName, string targetHosts, string targetPorts, string protocol, string action)
    {
        uint first;
        lock (_tableLock) first = _proxies.Count > 0 ? _proxies[0].Id : 0;
        return AddRuleEx(processName, targetHosts, targetPorts, protocol, action, first);
    }

    public bool DeleteRule(uint ruleId)
    {
        lock (_tableLock) return _rules.RemoveAll(r => r.Id == ruleId) > 0;
    }

    /// <summary>The recorded table as the JSON pbcore reads (internal/rules).</summary>
    private string BuildRulesJson()
    {
        lock (_tableLock)
        {
            var proxies = new JsonArray();
            foreach (var p in _proxies)
                proxies.Add(new JsonObject { ["id"] = p.Id, ["url"] = p.Url });
            var rules = new JsonArray();
            foreach (var r in _rules)
            {
                var o = new JsonObject { ["process"] = r.Process, ["action"] = r.Action };
                if (r.Action == "proxy") o["proxy"] = r.ProxyId;
                rules.Add(o);
            }
            return new JsonObject
            {
                ["proxies"] = proxies, ["rules"] = rules,
                ["udp"] = _udpMode, ["dns_via_proxy"] = _dnsViaProxy, ["dns_proxy"] = _dnsProxy
            }.ToJsonString();
        }
    }

    private int RuleCount()
    {
        lock (_tableLock) return _rules.Count;
    }

    private static string PbcorePath => Path.Combine(AppContext.BaseDirectory, "pbcore");

    // ------------------------------------------------------------------
    // Start / stop
    // ------------------------------------------------------------------

    public async Task<bool> StartAsync()
    {
        _lastError = "";
        if (_isRunning) return true;
        if (!File.Exists(PbcorePath))
        {
            _lastError = I18n.T("engine.mac.helper_missing", PbcorePath);
            Log(_lastError);
            return false;
        }

        // A per-session pbcore left over from a previous run (for example after a crash) would hold the port.
        await StopStaleInstanceAsync();

        var daemon = await StartViaDaemonAsync();
        if (daemon != DaemonOutcome.Unavailable) return daemon == DaemonOutcome.Started;
        return await StartLegacyAsync();
    }

    private enum DaemonOutcome { Started, Failed, Unavailable }

    /// <summary>True when the last start went through the installed daemon.</summary>
    public bool ViaDaemon => _viaDaemon;

    /// <summary>
    /// Connects through the installed daemon, installing or upgrading it first (one
    /// administrator prompt). Unavailable means the legacy per-session launch should be used.
    /// </summary>
    private async Task<DaemonOutcome> StartViaDaemonAsync()
    {
        var wanted = MacHelper.BundledVersion(PbcorePath);
        var token = MacHelper.ReadToken();
        var running = token != null ? MacHelper.Ping(token, TimeSpan.FromSeconds(1)) : null;
        if (running == null || (wanted.Length > 0 && running != wanted))
        {
            Log(running == null
                ? "Installing the system helper (one administrator prompt)..."
                : $"Upgrading the system helper {running} -> {wanted} (one administrator prompt)...");
            var res = await MacHelper.InstallAsync(PbcorePath);
            if (!res.Ok)
            {
                if (res.Cancelled)
                {
                    _lastError = I18n.T("engine.mac.admin_cancelled");
                    Log(_lastError);
                    return DaemonOutcome.Failed;
                }
                Log($"[helper] install failed: {res.Output}. Falling back to a per-session start.");
                return DaemonOutcome.Unavailable;
            }
            token = MacHelper.ReadToken();
            if (token == null)
            {
                Log("[helper] token not readable after install. Falling back to a per-session start.");
                return DaemonOutcome.Unavailable;
            }
            running = await MacHelper.WaitForDaemonAsync(token, TimeSpan.FromSeconds(10));
            if (running == null)
            {
                Log("[helper] daemon did not come up: " + MacHelper.TailLog() + ". Falling back to a per-session start.");
                return DaemonOutcome.Unavailable;
            }
            Log($"[helper] daemon {running} ready");
        }

        _token = token!;
        _viaDaemon = true;
        _stopRequested = false;
        _up = 0;
        _down = 0;

        var request = BuildStartJson();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
        Log($"Starting the tunnel through the system helper ({RuleCount()} rules)...");
        string? reply;
        try
        {
            reply = await Task.Run(() => MacHelper.SendCommand("start", _token, TimeSpan.FromSeconds(60), payload));
        }
        catch (Exception ex)
        {
            reply = "error " + ex.Message;
        }
        if (reply != "ok")
        {
            _lastError = I18n.T("engine.mac.start_failed", (reply ?? "no reply").Replace("error ", ""));
            Log(_lastError);
            return DaemonOutcome.Failed;
        }

        // The daemon replays its last status line ("up") to a new watcher.
        var client = await ConnectControlAsync(TimeSpan.FromSeconds(5));
        if (client == null)
        {
            _lastError = I18n.T("engine.mac.no_control");
            Log(_lastError);
            try { MacHelper.SendCommand("stop", _token, TimeSpan.FromSeconds(2)); } catch { }
            return DaemonOutcome.Failed;
        }
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"watch {_token}\n"));
        var reader = new StreamReader(stream, Encoding.UTF8);
        var upTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch = client;
        _watchCts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchLoop(reader, upTcs, _watchCts.Token));
        var first = await Task.WhenAny(upTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (first != upTcs.Task || !upTcs.Task.Result)
        {
            if (string.IsNullOrEmpty(_lastError)) _lastError = I18n.T("engine.mac.not_ready") + " " + MacHelper.TailLog();
            Log(_lastError);
            Stop();
            return DaemonOutcome.Failed;
        }
        _isRunning = true;
        return DaemonOutcome.Started;
    }

    /// <summary>The "start" request: the rule set plus per-session options (see core-go/session.go).</summary>
    private string BuildStartJson()
    {
        var node = JsonNode.Parse(BuildRulesJson())!.AsObject();
        node["dns"] = DefaultDns;
        node["parent_pid"] = Environment.ProcessId;
        return node.ToJsonString();
    }

    /// <summary>Legacy path: pbcore launched as root for this session only (password prompt each time).</summary>
    private async Task<bool> StartLegacyAsync()
    {
        _viaDaemon = false;

        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try { File.WriteAllText(_tokenFile, _token); SetPrivate(_tokenFile); } catch { }

        var rulesFile = Path.Combine(_dataDir, "pbcore.rules");
        var launcher = Path.Combine(_dataDir, "pbcore-launch.sh");
        try
        {
            File.WriteAllText(rulesFile, BuildRulesJson() + "\n");
            SetPrivate(rulesFile);

            var args = string.Join(" ",
                Q(PbcorePath),
                "--rules-file", Q(rulesFile),
                "--dns", DefaultDns,
                "--control-port", ControlPort.ToString(),
                "--control-token", _token,
                "--parent-pid", Environment.ProcessId.ToString(),
                "--log-level", "info");

            var script = new StringBuilder();
            script.Append("#!/bin/sh\n");
            script.Append("chmod 755 ").Append(Q(PbcorePath)).Append(" 2>/dev/null\n");
            script.Append("xattr -d com.apple.quarantine ").Append(Q(PbcorePath)).Append(" 2>/dev/null\n");
            // No nohup: under osascript there is no console, and macOS nohup then refuses to start the
            // command ("can't detach from console"). Ignoring SIGHUP here is inherited by pbcore instead.
            script.Append("trap '' HUP\n");
            script.Append(args).Append(" > ").Append(Q(_logFile)).Append(" 2>&1 < /dev/null &\n");
            script.Append("exit 0\n");
            File.WriteAllText(launcher, script.ToString());
            SetPrivate(launcher);
        }
        catch (Exception ex)
        {
            _lastError = I18n.T("engine.mac.launcher_failed", ex.Message);
            Log(_lastError);
            return false;
        }

        _stopRequested = false;
        _up = 0;
        _down = 0;
        Log($"Requesting administrator privileges to start pbcore ({RuleCount()} rules)...");
        var (ok, output) = await RunOsascriptAsync(launcher);
        try { File.Delete(launcher); } catch { }
        if (!ok)
        {
            _lastError = output.Contains("-128") || output.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                ? I18n.T("engine.mac.admin_cancelled")
                : I18n.T("engine.mac.start_failed", output);
            Log(_lastError);
            try { File.Delete(rulesFile); } catch { }
            return false;
        }

        // The helper opens its control socket before touching the network, so connect first
        // and then wait for the "up" line.
        var client = await ConnectControlAsync(TimeSpan.FromSeconds(8));
        if (client == null)
        {
            _lastError = I18n.T("engine.mac.no_control") + " " + TailLog();
            Log(_lastError);
            try { File.Delete(rulesFile); } catch { }
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
        try { File.Delete(rulesFile); } catch { }
        if (first != upTcs.Task || !upTcs.Task.Result)
        {
            if (string.IsNullOrEmpty(_lastError)) _lastError = I18n.T("engine.mac.not_ready") + " " + TailLog();
            Log(_lastError);
            Stop();
            return false;
        }
        _isRunning = true;
        return true;
    }

    /// <summary>Sends the recorded table to the running helper in one "rules" command.</summary>
    public async Task<bool> CommitRulesAsync()
    {
        if (!_isRunning || string.IsNullOrEmpty(_token))
        {
            _lastError = I18n.T("engine.mac.stopped");
            return false;
        }
        var json = BuildRulesJson();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string? reply;
        try
        {
            reply = await Task.Run(() => MacHelper.SendCommand("rules", _token, TimeSpan.FromSeconds(10), payload));
        }
        catch (Exception ex)
        {
            reply = "error " + ex.Message;
        }
        if (reply == "ok")
        {
            Log($"[pbcore] rules replaced ({RuleCount()} rules)");
            _lastError = "";
            return true;
        }
        _lastError = I18n.T("engine.mac.rules_failed", (reply ?? "no reply").Replace("error ", ""));
        Log(_lastError);
        return false;
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
                            Log($"[pbcore] up: tun={GetStr(root, "tun")} all other programs -> {GetStr(root, "proxy")} rules={GetInt(root, "rules")} procmap={GetStr(root, "procmap")}");
                            upTcs.TrySetResult(true);
                            break;
                        case "rules":
                            Log($"[pbcore] rules active: {GetInt(root, "rules")}");
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
                            exitMessage ??= I18n.T("engine.mac.stopped");
                            upTcs.TrySetResult(false);
                            if (_viaDaemon) return;
                            break;
                        case "idle":
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
            if (!ct.IsCancellationRequested) exitMessage ??= I18n.T("engine.mac.connection_lost", ex.Message);
        }
        finally
        {
            upTcs.TrySetResult(false);
            var wasRunning = _isRunning;
            _isRunning = false;
            if (wasRunning && !_stopRequested)
            {
                Stopped?.Invoke(exitMessage ?? I18n.T("engine.mac.exited"));
            }
        }
    }

    private static string GetStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    public bool Stop()
    {
        _stopRequested = true;
        var hadWatch = _watch != null;
        try
        {
            if (!string.IsNullOrEmpty(_token))
            {
                var reply = MacHelper.SendCommand("stop", _token, TimeSpan.FromSeconds(2));
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
        try { reply = MacHelper.SendCommand("stop", oldToken, TimeSpan.FromSeconds(1)); } catch { }
        if (reply != null)
        {
            Log("[pbcore] stopped a previous instance");
            await Task.Delay(1500);
        }
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
            return I18n.T("engine.mac.log_prefix") + " " + string.Join(" | ", lines, start, lines.Length - start);
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
