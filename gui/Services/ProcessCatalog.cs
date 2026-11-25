using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ProxyBridge.GUI.Services;

/// <summary>A running program for the app picker (one entry per exe name).</summary>
public sealed record RunningProgram(string ExeName, string Path, bool HasWindow);

/// <summary>
/// Lists programs for the app picker: one entry per program, with the image path when it
/// can be read without elevation. Programs with a window come first.
/// Windows: one entry per exe name: running programs with a window, then installed programs
/// (Start menu shortcuts and the App Paths registry key), then background processes. macOS: one entry per .app bundle (helpers
/// are folded into their bundle): the running ones first, then every app installed in the
/// Applications folders (so a program can get its rule before it is started), then plain
/// executables started by the user (homebrew tools, scripts).
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

        // Installed programs that are not running now, so a program can get its rule before it is started.
        var installed = new List<RunningProgram>();
        try
        {
            foreach (var path in WindowsInstalledExes())
            {
                var exe = System.IO.Path.GetFileName(path);
                if (byName.ContainsKey(exe) || Hidden.Contains(System.IO.Path.GetFileNameWithoutExtension(exe))) continue;
                if (installed.Any(i => string.Equals(i.ExeName, exe, StringComparison.OrdinalIgnoreCase))) continue;
                installed.Add(new RunningProgram(exe, path, HasWindow: false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Installed programs list failed: {ex.Message}");
        }

        result.AddRange(byName.Values.Where(r => r.HasWindow)
            .OrderBy(r => IsWindowsDir(r.Path))
            .ThenBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        result.AddRange(installed.OrderBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        result.AddRange(byName.Values.Where(r => !r.HasWindow)
            .OrderBy(r => IsWindowsDir(r.Path))
            .ThenBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Executables of installed programs: targets of the Start menu shortcuts (all users and the
    /// current one) and the App Paths registry key. Uninstallers and Windows' own tools are left out.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<string> WindowsInstalledExes()
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsWindowsDir(path)) return;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return;
            try { if (!File.Exists(path)) return; } catch { return; }
            if (seen.Add(path)) found.Add(path);
        }

        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        Add(k?.GetValue(null) as string);
                    }
                    catch { /* unreadable entry */ }
                }
            }
            catch { /* no access */ }
        }

        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null) shell = Activator.CreateInstance(type);
            if (shell != null)
            {
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                };
                var links = 0;
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                        { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 });
                    }
                    catch { continue; }
                    foreach (var lnk in files)
                    {
                        if (++links > 1500) break;
                        object? shortcut = null;
                        try
                        {
                            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                            if (shortcut == null) continue;
                            Add(shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string);
                        }
                        catch { /* broken shortcut */ }
                        finally
                        {
                            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Start menu scan failed: {ex.Message}");
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
        return found;
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

        // Installed apps that are not running now: the list would otherwise miss the very program
        // the user is about to set up.
        var installed = new List<RunningProgram>();
        foreach (var bundle in MacInstalledApps())
        {
            if (byKey.ContainsKey(bundle)) continue;
            if (selfBundle.Length > 0 && string.Equals(bundle, selfBundle, StringComparison.Ordinal)) continue;
            var name = System.IO.Path.GetFileName(bundle);
            if (MacHiddenNames.Contains(System.IO.Path.GetFileNameWithoutExtension(name))) continue;
            installed.Add(new RunningProgram(name, bundle, HasWindow: false));
        }

        result.AddRange(byKey.Values.Where(r => r.HasWindow)
            .OrderByDescending(r => r.Path.StartsWith("/Applications/", StringComparison.Ordinal))
            .ThenBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        result.AddRange(installed.OrderBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        result.AddRange(byKey.Values.Where(r => !r.HasWindow)
            .OrderBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// .app bundles in /Applications and ~/Applications, plus one level of subfolders
    /// (Utilities, vendor folders). Unreadable folders are skipped.
    /// </summary>
    private static List<string> MacInstalledApps()
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { "/Applications" };
        if (!string.IsNullOrEmpty(home)) roots.Add(System.IO.Path.Combine(home, "Applications"));
        foreach (var root in roots)
        {
            foreach (var entry in SafeDirs(root))
            {
                if (entry.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(entry)) found.Add(entry);
                    continue;
                }
                if (System.IO.Path.GetFileName(entry).StartsWith(".", StringComparison.Ordinal)) continue;
                foreach (var inner in SafeDirs(entry))
                    if (inner.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && seen.Add(inner)) found.Add(inner);
            }
        }
        return found;
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
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
