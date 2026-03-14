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
            
            // Инициализируем сервис
            viewModel.Initialize(proxyService);
            
            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            
            // Передаем окно в ViewModel
            viewModel.SetMainWindow(mainWindow);
            
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // save config during shutdown 
            desktop.ShutdownRequested += (s, e) =>
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel vm)
                {
                    vm.Cleanup();
                }
            };

            // Auto-connect to last proxy after window is shown
            mainWindow.Opened += async (s, e) =>
            {
                await viewModel.AutoConnectIfNeeded();
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
            if (desktop.MainWindow is MainWindow mw)
            {
                if (mw.DataContext is MainWindowViewModel vm)
                {
                    vm.Cleanup();
                }
                mw.ForceClose();
            }
            desktop.Shutdown();
        }
    }
}
