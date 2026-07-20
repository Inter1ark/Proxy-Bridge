using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ProxyBridge.GUI;

class Program
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "ProxyBridge_CRASH.txt"
    );

    [STAThread]
    public static void Main(string[] args)
    {
        // Catch ALL unhandled exceptions — write to Desktop crash log
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] UNHANDLED: {e.ExceptionObject}\n";
            Console.Error.WriteLine(msg);
            try { File.AppendAllText(CrashLog, msg); } catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] TASK UNOBSERVED: {e.Exception}\n";
            Console.Error.WriteLine(msg);
            try { File.AppendAllText(CrashLog, msg); } catch { }
            e.SetObserved(); // Prevent crash
        };

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex}\n";
            Console.Error.WriteLine(msg);
            try { File.AppendAllText(CrashLog, msg); } catch { }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
