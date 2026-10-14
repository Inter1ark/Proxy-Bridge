using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyBridge.GUI.Services;

/// <summary>A running program for the app picker (one entry per exe name).</summary>
public sealed record RunningProgram(string ExeName, string Path, bool HasWindow);

/// <summary>
/// Lists running programs for the app picker: one entry per program, with the image path when it
/// can be read without elevation. Programs with a window come first.
/// Windows: one entry per exe name. macOS: one entry per .app bundle (helpers are folded into their
/// bundle), plus plain executables started by the user (homebrew tools, scripts).
/// </summary>
public static class ProcessCatalog
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "system", "registry", "memory compression", "secure system", "smss", "csrss", "wininit",
        "services", "lsass", "lsaiso", "winlogon", "fontdrvhost", "dwm", "conhost", "sihost", "ctfmon",
        "taskhostw", "dllhost", "runtimebroker", "searchindexer", "searchprotocolhost", "searchfilterhost",
        "wudfhost", "audiodg", "spoolsv", "smartscreen", "securityhealthservice", "sgrmbroker",
        "applicationframehost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost",
        "systemsettingsbroker", "backgroundtaskhost", "wmiprvse", "dashost", "unsecapp", "lockapp"
    };

    public static List<RunningProgram> List()
    {
        if (OperatingSystem.IsMacOS()) return ListMac();
        var result = new List<RunningProgram>();
        if (!OperatingSystem.IsWindows()) return result;

        var self = Environment.ProcessId;
        var byName = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (Exception ex)
        {
            AppLog.Warn($"Process list failed: {ex.Message}");
            return result;
        }

        foreach (var p in procs)
        {
            try
            {
                if (p.Id == self || p.Id <= 4) continue;
                var name = p.ProcessName;
                if (string.IsNullOrWhiteSpace(name) || Hidden.Contains(name)) continue;
                var path = ImagePath(p.Id);
                if (string.IsNullOrEmpty(path)) continue; // protected or system process
                var exe = System.IO.Path.GetFileName(path);
                bool hasWindow;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { hasWindow = false; }

                if (byName.TryGetValue(exe, out var existing))
                {
                    if (hasWindow && !existing.HasWindow) byName[exe] = existing with { HasWindow = true };
                    continue;
                }
                byName[exe] = new RunningProgram(exe, path, hasWindow);
            }
            catch
            {
                // the process exited while we looked at it
            }
            finally
            {
                p.Dispose();
            }
        }

        result.AddRange(byName.Values
            .OrderByDescending(r => r.HasWindow)
            .ThenBy(r => IsWindowsDir(r.Path))
            .ThenBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    // ------------------------------------------------------------------
    // macOS
    // ------------------------------------------------------------------

    /// <summary>Directories whose programs are system services, not something a user routes.</summary>
    private static readonly string[] MacSystemPrefixes =
    {
        "/System/", "/usr/libexec/", "/usr/sbin/", "/sbin/", "/Library/Apple/", "/Library/PrivilegedHelperTools/",
        "/usr/local/libexec/", "/opt/homebrew/libexec/", "/Library/Application Support/",
        "/private/var/", "/var/", "/Library/Frameworks/", "/usr/lib/"
    };

    /// <summary>Executable names that are shells or launchers, not programs anybody wants to route.</summary>
    private static readonly HashSet<string> MacHiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "zsh", "sh", "fish", "login", "ssh-agent", "launchd", "sudo", "su", "ps", "distnoted",
        "cfprefsd", "mdworker", "mdworker_shared", "trustd", "secd", "nsurlsessiond", "tccd",
        "lsd", "iconservicesagent", "pbcore", "ProxyBridge"
    };

    /// <summary>
    /// "ps -axo pid=,comm=" prints the full executable path of every process on macOS. Helpers inside
    /// an .app bundle are folded into the outermost bundle, which is what the core matches on too.
    /// </summary>
    private static List<RunningProgram> ListMac()
    {
        var result = new List<RunningProgram>();
        string output;
        try
        {
            var psi = new ProcessStartInfo("/bin/ps", "-axo pid=,comm=")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return result;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ps failed: {ex.Message}");
            return result;
        }

        var selfBundle = MacBundlePath(Environment.ProcessPath ?? "");
        var byKey = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var path = line.Substring(sp + 1).Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal)) continue;
            if (MacSystemPrefixes.Any(pre => path.StartsWith(pre, StringComparison.Ordinal))) continue;

            var bundle = MacBundlePath(path);
            if (bundle.Length > 0)
            {
                if (selfBundle.Length > 0 && string.Equals(bundle, selfBundle, StringComparison.Ordinal)) continue;
                // bundles that live inside another bundle or inside the system are not user programs
                if (bundle.StartsWith("/System/", StringComparison.Ordinal)) continue;
                var name = System.IO.Path.GetFileName(bundle); // "Telegram.app"
                if (MacHiddenNames.Contains(System.IO.Path.GetFileNameWithoutExtension(name))) continue;
                if (!byKey.ContainsKey(bundle))
                    byKey[bundle] = new RunningProgram(name, bundle, HasWindow: true);
                continue;
            }

            var exe = System.IO.Path.GetFileName(path);
            if (exe.Length == 0 || MacHiddenNames.Contains(exe)) continue;
            if (!byKey.ContainsKey(path))
                byKey[path] = new RunningProgram(exe, path, HasWindow: false);
        }

        result.AddRange(byKey.Values
            .OrderByDescending(r => r.HasWindow)
            .ThenByDescending(r => r.Path.StartsWith("/Applications/", StringComparison.Ordinal))
            .ThenBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>"/Applications/Google Chrome.app" for any executable inside that bundle, or "".</summary>
    public static string MacBundlePath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "";
        var i = exePath.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return exePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? exePath : "";
        return exePath.Substring(0, i + 4);
    }

    private static bool IsWindowsDir(string path)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return win.Length > 0 && path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full image path of a process (limited query rights), or null.</summary>
    public static string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
            {
                var path = sb.ToString(0, (int)size);
                return File.Exists(path) ? path : null;
            }
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
