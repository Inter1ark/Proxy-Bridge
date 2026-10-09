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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
