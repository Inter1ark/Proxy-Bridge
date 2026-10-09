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

            // Language: the user's choice from config.json, or ru/en by the system UI culture on first run.
            I18n.Instance.InitializeFromConfig();

            SetupTrayIcon();

            // Before the updater closes the app to install: stop system mode and Split Tunnel so
            // traffic interception is released. Errors are ignored (the updater also has a timeout).
            AutoUpdateService.BeforeInstall = async () =>
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d &&
                            d.MainWindow?.DataContext is MainWindowViewModel vm)
                        {
                            await vm.StopAllConnectionsAsync();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] BeforeInstall failed: {ex.Message}");
                }
            };

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
                    desktop.MainWindow = CreateLicenseWindow(autoVerify: true, initialMessageKey: null);
                    break;

                default:
                    desktop.MainWindow = CreateLicenseWindow(autoVerify: false, initialMessageKey: null);
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

        viewModel.Initialize(proxyService);

        var mainWindow = new MainWindow
        {
            DataContext = viewModel
        };

        viewModel.SetMainWindow(mainWindow);

        // Auto-connect to last proxy after window is shown
        mainWindow.Opened += async (s, e) =>
        {
            await viewModel.AutoConnectIfNeeded();
        };

        // Startup update check (once per app run, fire-and-forget on the UI thread)
        mainWindow.Opened += (s, e) =>
        {
            if (_startupUpdateCheckDone) return;
            _startupUpdateCheckDone = true;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (TryShowUpdateDemo()) return;
                    if (await UpdateViewModel.Shared.CheckOnStartupAsync())
                        ShowUpdateWindow(checkNow: false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] startup check failed: {ex.Message}");
                }
            });
        };

        return mainWindow;
    }

    private LicenseWindow CreateLicenseWindow(bool autoVerify, string? initialMessageKey)
    {
        var vm = new LicenseViewModel(LicenseService.Instance, autoVerify, initialMessageKey);
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
    /// or when the server rejects the cached license). The message is an i18n key.
    /// </summary>
    public void ShowLicenseWindow(string? messageKey = null)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        _updateWindow?.Close();
        var previous = desktop.MainWindow;
        var licenseWindow = CreateLicenseWindow(autoVerify: false, initialMessageKey: messageKey);
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
            await Dispatcher.UIThread.InvokeAsync(() => ShowLicenseWindow(LicenseService.ErrorToKey(result.Error)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] background verify failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Update window
    // ------------------------------------------------------------------

    private bool _startupUpdateCheckDone;
    private UpdateWindow? _updateWindow;

    /// <summary>
    /// Opens the update window (a single instance) bound to UpdateViewModel.Shared.
    /// With checkNow the manual check is started as well ("Check now" in Settings).
    /// </summary>
    public void ShowUpdateWindow(bool checkNow)
    {
        if (_updateWindow == null)
        {
            _updateWindow = new UpdateWindow();
            _updateWindow.Closed += (_, _) => _updateWindow = null;
            var owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is { IsVisible: true })
            {
                _updateWindow.Show(owner);
            }
            else
            {
                _updateWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _updateWindow.Show();
            }
        }
        else
        {
            _updateWindow.Activate();
        }

        if (checkNow)
        {
            var cmd = UpdateViewModel.Shared.CheckCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
        }
    }

    /// <summary>
    /// UI preview hook for screenshots. PROXYBRIDGE_UI_DEMO_UPDATE=1 (or a state name: checking, uptodate,
    /// downloading, verifying, installing, error, mandatory) fills UpdateViewModel.Shared with sample
    /// data and opens the window. It never downloads or installs anything by itself, and does nothing
    /// when the variable is not set.
    /// </summary>
    private bool TryShowUpdateDemo()
    {
        var demo = Environment.GetEnvironmentVariable("PROXYBRIDGE_UI_DEMO_UPDATE");
        if (string.IsNullOrWhiteSpace(demo)) return false;

        var u = UpdateViewModel.Shared;
        u.CurrentVersion = LicenseService.AppVersion;
        u.NewVersion = "3.3.0";
        u.PublishedAt = "2026-10-01T12:00:00Z";
        u.NotesRu = string.Join("\n",
            "Новый интерфейс, полностью на русском и английском",
            "Автоматические обновления прямо из приложения",
            "Быстрее подключение в режиме выбранных программ",
            "Исправлена ошибка с DNS после спящего режима");
        u.NotesEn = string.Join("\n",
            "New interface, fully in Russian and English",
            "Automatic updates right from the app",
            "Faster connection in selected apps mode",
            "Fixed a DNS issue after sleep");
        u.IsMandatory = false;
        u.BytesTotal = 38L * 1024 * 1024;
        u.BytesReceived = 0;
        u.Progress = 0;
        u.ErrorCode = "";

        switch (demo.Trim().ToLowerInvariant())
        {
            case "checking":
                u.State = UpdateState.Checking;
                break;
            case "uptodate":
                u.NewVersion = "";
                u.State = UpdateState.UpToDate;
                break;
            case "downloading":
                u.BytesReceived = 15L * 1024 * 1024 + 300 * 1024;
                u.Progress = 100.0 * u.BytesReceived / u.BytesTotal;
                u.State = UpdateState.Downloading;
                break;
            case "verifying":
                u.State = UpdateState.Verifying;
                break;
            case "installing":
                u.State = UpdateState.Installing;
                break;
            case "error":
                u.ErrorCode = "hash_mismatch";
                u.State = UpdateState.Error;
                break;
            case "mandatory":
                u.IsMandatory = true;
                u.State = UpdateState.Available;
                break;
            default:
                u.State = UpdateState.Available;
                break;
        }

        ShowUpdateWindow(checkNow: false);
        return true;
    }

    // ------------------------------------------------------------------
    // Tray
    // ------------------------------------------------------------------

    private NativeMenuItem? _trayOpen;
    private NativeMenuItem? _trayExit;

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
            _trayOpen = new NativeMenuItem(I18n.T("tray.open"));
            _trayOpen.Click += TrayIcon_Show;
            _trayExit = new NativeMenuItem(I18n.T("tray.exit"));
            _trayExit.Click += TrayIcon_Exit;
            var menu = new NativeMenu();
            menu.Items.Add(_trayOpen);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(_trayExit);
            icon.Menu = menu;
            TrayIcon.SetIcons(this, new TrayIcons { icon });

            I18n.Instance.LanguageChanged += () =>
            {
                if (_trayOpen != null) _trayOpen.Header = I18n.T("tray.open");
                if (_trayExit != null) _trayExit.Header = I18n.T("tray.exit");
            };
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
