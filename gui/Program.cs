using Avalonia;
using ProxyBridge.GUI.Services;
using System;
using System.Threading.Tasks;

namespace ProxyBridge.GUI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Unhandled errors go to the support log (Settings, "Open logs folder").
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.Error.WriteLine($"UNHANDLED: {e.ExceptionObject}");
            AppLog.Error($"UNHANDLED: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            AppLog.Error($"TASK UNOBSERVED: {e.Exception}");
            e.SetObserved(); // Prevent crash
        };

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            AppLog.Error($"FATAL: {ex}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (OperatingSystem.IsMacOS())
            builder = builder.With(new AvaloniaNativePlatformOptions { RenderingMode = MacRenderingModes() });
        return builder.WithInterFont().LogToTrace();
    }

    /// <summary>
    /// macOS renderer order. Metal leaked a window-sized surface per rendered frame (tens of GB
    /// after a couple of hours), so OpenGL comes first and software is the fallback.
    /// PB_MAC_RENDER=metal|opengl|software overrides it for diagnostics.
    /// </summary>
    private static AvaloniaNativeRenderingMode[] MacRenderingModes()
    {
        switch ((Environment.GetEnvironmentVariable("PB_MAC_RENDER") ?? "").Trim().ToLowerInvariant())
        {
            case "metal": return new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software };
            case "software": return new[] { AvaloniaNativeRenderingMode.Software };
            default: return new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software };
        }
    }
}
