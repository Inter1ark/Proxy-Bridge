using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// The macOS "system helper": pbcore installed as a launchd daemon
/// (mac/install-helper.sh) so connecting needs no administrator password.
/// The daemon listens on a unix socket (and on the localhost control port for
/// tools); the token file is what authorises a client. The unix socket is used
/// first: a VPN with a packet filter can drop loopback TCP, which once left the
/// app spinning on "connecting" with a VPN in the background.
/// </summary>
public static class MacHelper
{
    public const string Label = "org.proxybridge.pbcore";
    public const string TokenPath = "/Library/Application Support/ProxyBridge/control.token";
    public const string PlistPath = "/Library/LaunchDaemons/" + Label + ".plist";
    public const string HelperPath = "/Library/PrivilegedHelperTools/" + Label;
    public const string LogPath = "/Library/Logs/ProxyBridge/pbcore.log";
    public const string SocketPath = "/Library/Application Support/ProxyBridge/control.sock";
    public const int ControlPort = 34050;

    private static string ScriptPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    /// <summary>True when the launchd job is registered (the daemon may still be starting).</summary>
    public static bool IsInstalled => OperatingSystem.IsMacOS() && File.Exists(PlistPath);

    /// <summary>The daemon token, or null when there is none or this user may not read it.</summary>
    public static string? ReadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var t = File.ReadAllText(TokenPath).Trim();
            return t.Length > 0 ? t : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks the daemon for its version; null when nothing answers on the control port.</summary>
    public static string? Ping(string token, TimeSpan timeout)
    {
        try
        {
            var reply = SendCommand("ping", token, timeout);
            if (reply == null || !reply.StartsWith("ok", StringComparison.Ordinal)) return null;
            var parts = reply.Split(' ', 2);
            return parts.Length == 2 ? parts[1].Trim() : "";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Waits until the daemon answers ping (after install or reboot).</summary>
    public static async Task<string?> WaitForDaemonAsync(string token, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var v = Ping(token, TimeSpan.FromSeconds(1));
            if (v != null) return v;
            await Task.Delay(300);
        }
        return null;
    }

    /// <summary>Installs or upgrades the daemon with one administrator prompt.</summary>
    public static Task<OsascriptResult> InstallAsync(string pbcorePath)
        => RunAdminScriptAsync(ScriptPath("install-helper.sh"), pbcorePath);

    /// <summary>Removes the daemon, its token and launchd job with one administrator prompt.</summary>
    public static Task<OsascriptResult> UninstallAsync()
        => RunAdminScriptAsync(ScriptPath("uninstall-helper.sh"), null);

    /// <summary>The version of the bundled pbcore ("pbcore X.Y.Z"), or "" when it cannot be run.</summary>
    public static string BundledVersion(string pbcorePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pbcorePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            var parts = output.Split(' ', 2);
            return parts.Length == 2 ? parts[1].Trim() : output;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>One connection to the daemon's control socket (unix socket or loopback TCP).</summary>
    public sealed class ControlConnection : IDisposable
    {
        private readonly Socket _socket;
        public Stream Stream { get; }
        public string Via { get; }

        private ControlConnection(Socket socket, string via)
        {
            _socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: true);
            Via = via;
        }

        /// <summary>
        /// Connects within the timeout: the unix socket when its file exists, otherwise
        /// (or when the unix socket refuses) loopback TCP. Null when nothing answers in time.
        /// A connect that never completes is abandoned, not left to fault unobserved.
        /// </summary>
        public static async Task<ControlConnection?> ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            if (File.Exists(SocketPath))
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token).ConfigureAwait(false);
                    return new ControlConnection(s, "unix");
                }
                catch (OperationCanceledException)
                {
                    s.Dispose();
                    return null;
                }
                catch
                {
                    s.Dispose(); // refused or not permitted: the TCP port may still answer
                }
            }
            var t = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await t.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ControlPort), cts.Token).ConfigureAwait(false);
                return new ControlConnection(t, "tcp");
            }
            catch
            {
                t.Dispose();
                return null;
            }
        }

        /// <summary>Blocking variant. Runs on the thread pool so a caller on the UI thread cannot deadlock on the continuation.</summary>
        public static ControlConnection? Connect(TimeSpan timeout)
            => Task.Run(() => ConnectAsync(timeout)).GetAwaiter().GetResult();

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { _socket.Dispose(); } catch { }
        }
    }

    public static string? SendCommand(string command, string token, TimeSpan timeout, string? argument = null)
    {
        using var conn = ControlConnection.Connect(timeout);
        if (conn == null) return null;
        var stream = conn.Stream;
        stream.ReadTimeout = (int)timeout.TotalMilliseconds;
        stream.WriteTimeout = (int)timeout.TotalMilliseconds;
        var line = argument == null ? $"{command} {token}\n" : $"{command} {token} {argument}\n";
        var data = Encoding.ASCII.GetBytes(line);
        stream.Write(data, 0, data.Length);
        var buf = new byte[4096];
        var n = stream.Read(buf, 0, buf.Length);
        return n > 0 ? Encoding.ASCII.GetString(buf, 0, n).Trim() : "";
    }

    public readonly record struct OsascriptResult(bool Ok, bool Cancelled, string Output);

    /// <summary>Time for the password prompt plus the script itself; a stuck script is killed after it.</summary>
    public static TimeSpan AdminScriptTimeout { get; set; } = TimeSpan.FromSeconds(180);

    private static Process? _adminProcess;

    /// <summary>Aborts a running administrator script (closes the prompt, kills the script).</summary>
    public static void CancelAdminScript()
    {
        try { _adminProcess?.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Runs "/bin/sh script [arg]" with administrator privileges (one password prompt).</summary>
    public static async Task<OsascriptResult> RunAdminScriptAsync(string scriptPath, string? arg)
    {
        if (!File.Exists(scriptPath))
            return new OsascriptResult(false, false, $"script not found: {scriptPath}");
        var shell = "/bin/sh " + Quoted(scriptPath) + (arg == null ? "" : " " + Quoted(arg));
        var apple = "do shell script \"" + shell.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" with administrator privileges";
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
            if (p == null) return new OsascriptResult(false, false, "osascript could not be started");
            _adminProcess = p;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(AdminScriptTimeout);
            try
            {
                await p.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                _adminProcess = null;
                return new OsascriptResult(false, false, $"the helper script did not finish in {AdminScriptTimeout.TotalSeconds:0} s and was stopped");
            }
            _adminProcess = null;
            var output = (await stdout + "\n" + await stderr).Trim();
            var cancelled = p.ExitCode != 0 && (output.Contains("-128") || output.Contains("canceled", StringComparison.OrdinalIgnoreCase));
            return new OsascriptResult(p.ExitCode == 0, cancelled, output);
        }
        catch (Exception ex)
        {
            return new OsascriptResult(false, false, ex.Message);
        }
    }

    private static string Quoted(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Last lines of the daemon log for error messages.</summary>
    public static string TailLog(int lines = 5)
    {
        try
        {
            if (!File.Exists(LogPath)) return "";
            var all = File.ReadAllLines(LogPath);
            var start = Math.Max(0, all.Length - lines);
            return string.Join(" | ", all, start, all.Length - start);
        }
        catch
        {
            return "";
        }
    }
}
