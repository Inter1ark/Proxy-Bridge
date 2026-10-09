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
/// Lists running programs for the app picker: one entry per exe name, with the image path when it
/// can be read without elevation. Programs with a window come first. Windows only (empty elsewhere).
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
