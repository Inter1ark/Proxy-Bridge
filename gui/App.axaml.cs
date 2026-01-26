using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using ProxyBridge.GUI.ViewModels;
using ProxyBridge.GUI.Views;
using ProxyBridge.GUI.Services;
using System;

namespace ProxyBridge.GUI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            var proxyService = new ProxyBridgeService();
            
            // Логи отключены для release версии
            // Если нужно включить - раскомментируйте код ниже
            /*
            proxyService.LogReceived += (message) =>
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyBridge] {message}");
            };
            
            proxyService.ConnectionReceived += (processName, pid, destIp, destPort, proxyInfo) =>
            {
                System.Diagnostics.Debug.WriteLine($"[Connection] {processName} (PID:{pid}) -> {destIp}:{destPort} | {proxyInfo}");
            };
            */
            
            // Инициализируем сервис
            viewModel.Initialize(proxyService);
            
            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            
            // Передаем окно в ViewModel
            viewModel.SetMainWindow(mainWindow);
            
            desktop.MainWindow = mainWindow;

            // save config during shutdown
            desktop.ShutdownRequested += (s, e) =>
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel vm)
                {
                    vm.Cleanup();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
    // https://docs.avaloniaui.net/docs/reference/controls/tray-icon
    public void TrayIcon_Show(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }
    }

    public void TrayIcon_Exit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
