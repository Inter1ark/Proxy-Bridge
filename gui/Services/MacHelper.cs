using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// The macOS "system helper": pbcore installed as a launchd daemon
/// (mac/install-helper.sh) so connecting needs no administrator password.
/// The daemon listens on the localhost control port; its token is readable by
/// administrators only, which is what makes the socket safe to expose.
/// </summary>
public static class MacHelper
{
    public const string Label = "org.proxybridge.pbcore";
    public const string TokenPath = "/Library/Application Support/ProxyBridge/control.token";
    public const string PlistPath = "/Library/LaunchDaemons/" + Label + ".plist";
    public const string HelperPath = "/Library/PrivilegedHelperTools/" + Label;
    public const string LogPath = "/Library/Logs/ProxyBridge/pbcore.log";
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

    public static string? SendCommand(string command, string token, TimeSpan timeout, string? argument = null)
    {
        using var client = new TcpClient();
        var connect = client.ConnectAsync("127.0.0.1", ControlPort);
        if (!connect.Wait(timeout)) return null;
        using var stream = client.GetStream();
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
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
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
