using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Threading;
using ProxyBridge.GUI.ViewModels;
using ProxyBridge.GUI.Views;
using ProxyBridge.GUI.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

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
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetupTrayIcon();

            // save config during shutdown
            desktop.ShutdownRequested += (s, e) =>
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel vm)
                {
                    vm.Cleanup();
                }
            };

            // Decide from the local cache only (no network on the UI thread):
            //  - no key                         -> activation window
            //  - cache fresh (< 7 days, valid)  -> main window + background verify
            //  - cache stale / expired locally  -> activation window that verifies online on open
            var decision = LicenseService.Instance.GetStartupDecision();
            switch (decision)
            {
                case LicenseStartupDecision.LicensedCached:
                    desktop.MainWindow = CreateMainWindow();
                    _ = VerifyLicenseInBackgroundAsync();
                    break;

                case LicenseStartupDecision.NeedsOnlineVerify:
                    desktop.MainWindow = CreateLicenseWindow(autoVerify: true, initialMessage: null);
                    break;

                default:
                    desktop.MainWindow = CreateLicenseWindow(autoVerify: false, initialMessage: null);
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ------------------------------------------------------------------
    // Window factories / switching
    // ------------------------------------------------------------------

    private MainWindow CreateMainWindow()
    {
        var viewModel = new MainWindowViewModel();
        // Windows: native ProxyBridgeCore.dll (WinDivert). macOS: bundled pbcore helper.
        // The native P/Invoke path is never touched on macOS.
        IProxyEngine proxyService = OperatingSystem.IsMacOS()
            ? new MacProxyEngine()
            : new WindowsProxyEngine();

        // Инициализируем сервис
        viewModel.Initialize(proxyService);

        var mainWindow = new MainWindow
        {
            DataContext = viewModel
        };

        // Передаем окно в ViewModel
        viewModel.SetMainWindow(mainWindow);

        // Auto-connect to last proxy after window is shown
        mainWindow.Opened += async (s, e) =>
        {
            await viewModel.AutoConnectIfNeeded();
        };

        return mainWindow;
    }

    private LicenseWindow CreateLicenseWindow(bool autoVerify, string? initialMessage)
    {
        var vm = new LicenseViewModel(LicenseService.Instance, autoVerify, initialMessage);
        return new LicenseWindow { DataContext = vm };
    }

    /// <summary>Creates and shows the main window (after a successful activation) and makes it the lifetime's MainWindow.</summary>
    public void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        if (desktop.MainWindow is MainWindow existing)
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var mainWindow = CreateMainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// Replaces the main window with the activation window (used after unlinking the device
    /// or when the server rejects the cached license).
    /// </summary>
    public void ShowLicenseWindow(string? message = null)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var previous = desktop.MainWindow;
        var licenseWindow = CreateLicenseWindow(autoVerify: false, initialMessage: message);
        desktop.MainWindow = licenseWindow;
        licenseWindow.Show();

        if (previous is MainWindow mw)
        {
            mw.ForceClose();
        }
        else if (previous != null && !ReferenceEquals(previous, licenseWindow))
        {
            previous.Close();
        }
    }

    /// <summary>Background re-check of a cached license. On ok:false the license is cleared and the activation window is shown.</summary>
    private async Task VerifyLicenseInBackgroundAsync()
    {
        try
        {
            var result = await LicenseService.Instance.VerifyAsync();

            if (result.Ok)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                        desktop.MainWindow?.DataContext is MainWindowViewModel vm)
                    {
                        vm.RefreshLicenseInfo();
                    }
                });
                return;
            }

            if (result.IsNetworkError)
            {
                // Server unreachable: the fresh cache stays valid.
                return;
            }

            // Server said ok:false -> VerifyAsync already cleared the cache; show the activation window.
            await Dispatcher.UIThread.InvokeAsync(() => ShowLicenseWindow(result.Message));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] background verify failed: {ex.Message}");
        }
    }

    // https://docs.avaloniaui.net/docs/reference/controls/tray-icon
    // Created in code (not in App.axaml) so a platform without tray support cannot break startup.
    private void SetupTrayIcon()
    {
        try
        {
            var icon = new TrayIcon
            {
                ToolTipText = "ProxyBridge",
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ProxyBridge/Assets/logo.ico")))
            };
            var open = new NativeMenuItem("Open");
            open.Click += TrayIcon_Show;
            var exit = new NativeMenuItem("Exit");
            exit.Click += TrayIcon_Exit;
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);
            icon.Menu = menu;
            TrayIcon.SetIcons(this, new TrayIcons { icon });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Tray] tray icon unavailable: {ex.Message}");
        }
    }

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
