using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// Real program icons for the routing diagram (Windows only). The icon comes from the running
/// process image if the program is running, otherwise from the stored exe path. Results (including
/// "no icon") are cached per process name; lookups run on a worker thread.
/// </summary>
public static class AppIconService
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<Bitmap?>>> Waiters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calls <paramref name="onReady"/> on the UI thread with the icon or null. Cached results are returned synchronously.</summary>
    public static void Request(string processName, string? exePath, Action<Bitmap?> onReady)
    {
        if (string.IsNullOrWhiteSpace(processName) || !OperatingSystem.IsWindows())
        {
            onReady(null);
            return;
        }

        var key = processName.Trim();
        if (Cache.TryGetValue(key, out var cached))
        {
            onReady(cached);
            return;
        }

        if (!Waiters.TryGetValue(key, out var list))
        {
            list = new List<Action<Bitmap?>>();
            Waiters[key] = list;
        }
        list.Add(onReady);
        if (!Pending.Add(key)) return;

        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = Load(key, exePath); } catch { bmp = null; }
            Dispatcher.UIThread.Post(() =>
            {
                Cache[key] = bmp;
                Pending.Remove(key);
                if (Waiters.Remove(key, out var callbacks))
                    foreach (var cb in callbacks) cb(bmp);
            });
        });
    }

    /// <summary>Forgets the cached icon of a program (for example after its exe path changed).</summary>
    public static void Invalidate(string processName)
    {
        if (!string.IsNullOrWhiteSpace(processName)) Cache.Remove(processName.Trim());
    }

    private static Bitmap? Load(string processName, string? exePath)
    {
#if WINDOWS
        var path = FindRunningImage(processName);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            path = exePath;
        if (string.IsNullOrEmpty(path)) return null;

        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon == null) return null;
        using var bitmap = icon.ToBitmap();
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return new Bitmap(ms);
#else
        return null;
#endif
    }

#if WINDOWS
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    /// <summary>Image path of a running process with this name (limited query rights, works without elevation for most processes).</summary>
    private static string? FindRunningImage(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrEmpty(name)) return null;
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); }
        catch { return null; }

        try
        {
            foreach (var p in procs)
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
                    {
                        var path = sb.ToString(0, (int)size);
                        if (File.Exists(path)) return path;
                    }
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return null;
    }
#endif
}
