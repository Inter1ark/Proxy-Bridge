using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace ProxyBridge.GUI;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllocConsole();

    [STAThread]
    public static void Main(string[] args)
    {
        // Открываем консоль для логов
        AllocConsole();
        Console.WriteLine("=== ProxyBridge Debug Console ===");
        Console.WriteLine("Time: " + DateTime.Now);
        Console.WriteLine("================================\n");

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
