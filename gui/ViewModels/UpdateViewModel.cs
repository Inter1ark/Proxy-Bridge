using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

/// <summary>
/// Contract between the auto-update logic and the update window.
/// The view model holds state and data only; all user-visible text lives in the UI layer
/// (localized by State and ErrorCode), so this class never contains display strings.
/// </summary>
public enum UpdateState
{
    Idle,          // nothing checked yet
    Checking,      // contacting the update server
    UpToDate,      // current version is the latest
    Available,     // newer version found, waiting for the user
    Downloading,   // installer download in progress (see Progress, BytesReceived, BytesTotal)
    Verifying,     // checking SHA-256 of the downloaded installer
    Installing,    // installer launched, the app is about to close and restart
    Error          // see ErrorCode
}

public class UpdateViewModel : ViewModelBase
{
    private UpdateState _state = UpdateState.Idle;
    private string _currentVersion = "";
    private string _newVersion = "";
    private string _notesRu = "";
    private string _notesEn = "";
    private string _publishedAt = "";
    private double _progress;
    private long _bytesReceived;
    private long _bytesTotal;
    private string _errorCode = "";
    private bool _isMandatory;
    private bool _autoCheck = true;

    // Logic (AutoUpdateService.cs / UpdateStore.cs). The hook to stop interception before installing is
    // AutoUpdateService.BeforeInstall.
    private readonly AutoUpdateService _service;
    private readonly UpdateStore _store;
    private UpdateManifest? _manifest;
    private int _busy; // 0 = idle, 1 = a check or an install is running

    /// <summary>One shared instance for the whole app (startup check, Settings, update window).</summary>
    public static UpdateViewModel Shared { get; } = new UpdateViewModel();

    /// <summary>"Check for updates automatically" switch in Settings; persisted by the update logic.</summary>
    public virtual bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (SetProperty(ref _autoCheck, value))
                _store.Update(p => p.AutoCheck = value);
        }
    }

    /// <summary>
    /// Called once after the main window is shown. Returns true when the update window should be opened
    /// (auto-check enabled, a newer version exists and it was not skipped, or the update is mandatory).
    /// </summary>
    public virtual async Task<bool> CheckOnStartupAsync()
    {
        try
        {
            var result = await CheckCoreAsync(manual: false).ConfigureAwait(false);
            if (result == null) return false;
            var prefs = _store.Load();
            return AutoUpdateService.ShouldPrompt(result, prefs.AutoCheck, prefs.SkippedVersion);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Current state; the window switches its content by this value.</summary>
    public UpdateState State { get => _state; set { if (SetProperty(ref _state, value)) OnStateChanged(); } }

    public string CurrentVersion { get => _currentVersion; set => SetProperty(ref _currentVersion, value); }
    public string NewVersion { get => _newVersion; set => SetProperty(ref _newVersion, value); }

    /// <summary>Release notes in both languages (plain text, one change per line). The UI picks by current language.</summary>
    public string NotesRu { get => _notesRu; set => SetProperty(ref _notesRu, value); }
    public string NotesEn { get => _notesEn; set => SetProperty(ref _notesEn, value); }

    /// <summary>ISO date of the release, may be empty.</summary>
    public string PublishedAt { get => _publishedAt; set => SetProperty(ref _publishedAt, value); }

    /// <summary>Download progress 0..100.</summary>
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public long BytesReceived { get => _bytesReceived; set => SetProperty(ref _bytesReceived, value); }
    public long BytesTotal { get => _bytesTotal; set => SetProperty(ref _bytesTotal, value); }

    /// <summary>Machine-readable error: network, hash_mismatch, download_failed, install_failed, unsupported_platform, server.</summary>
    public string ErrorCode { get => _errorCode; set => SetProperty(ref _errorCode, value); }

    /// <summary>True when the current version is below the manifest's min_supported: the window should not offer "Later".</summary>
    public bool IsMandatory { get => _isMandatory; set => SetProperty(ref _isMandatory, value); }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing;
    public bool CanInstall => State is UpdateState.Available or UpdateState.Error && !string.IsNullOrEmpty(NewVersion);

    /// <summary>Check the server now (manual "Check for updates").</summary>
    public ICommand CheckCommand { get; protected set; }

    /// <summary>Download, verify and install silently, then close the app and relaunch the new version.</summary>
    public ICommand InstallCommand { get; protected set; }

    /// <summary>Remember this version as skipped and close the window (hidden when IsMandatory).</summary>
    public ICommand SkipCommand { get; protected set; }

    /// <summary>Close the window and remind on the next start.</summary>
    public ICommand LaterCommand { get; protected set; }

    /// <summary>Raised when the window should close (after Skip, Later, or right before the app exits to install).</summary>
    public event EventHandler? RequestClose;

    protected void RaiseRequestClose() => RequestClose?.Invoke(this, EventArgs.Empty);

    public UpdateViewModel()
    {
        _service = new AutoUpdateService();
        _store = new UpdateStore();
        _currentVersion = AutoUpdateService.FormatVersion(_service.CurrentVersion);
        _autoCheck = _store.Load().AutoCheck;

        CheckCommand = new RelayCommand(() => _ = CheckCoreAsync(manual: true));
        InstallCommand = new RelayCommand(() => _ = InstallAsync());
        SkipCommand = new RelayCommand(Skip);
        LaterCommand = new RelayCommand(RaiseRequestClose);
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInstall));
    }

    // ------------------------------------------------------------------
    // Logic
    // ------------------------------------------------------------------

    /// <summary>Runs on the UI thread (directly when already there).</summary>
    private static void Ui(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch
        {
            // no dispatcher (shutting down): ignore
        }
    }

    private static Task UiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private void SetError(string code) => Ui(() =>
    {
        ErrorCode = code;
        State = UpdateState.Error;
    });

    /// <summary>
    /// Fetches the manifest and fills the properties. Manual checks report errors through State/ErrorCode;
    /// the startup check fails silently (state goes back to what it was). Never throws.
    /// </summary>
    private async Task<UpdateCheckResult?> CheckCoreAsync(bool manual)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        var previous = State;
        try
        {
            await UiAsync(() => { ErrorCode = ""; State = UpdateState.Checking; }).ConfigureAwait(false);
            var result = await _service.CheckAsync().ConfigureAwait(false);
            _manifest = result.Manifest;
            _store.Update(p => p.LastCheckUtc = DateTime.UtcNow);
            await UiAsync(() => Apply(result)).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            var code = ex is UpdateException ue ? ue.Code : "network";
            if (manual) SetError(code);
            else Ui(() => State = previous == UpdateState.Checking ? UpdateState.Idle : previous);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Apply(UpdateCheckResult r)
    {
        var m = r.Manifest;
        CurrentVersion = AutoUpdateService.FormatVersion(r.Current);
        NewVersion = r.IsNewer ? r.LatestText : "";
        NotesRu = r.IsNewer ? string.Join("\n", m.Notes?.Ru ?? new()) : "";
        NotesEn = r.IsNewer ? string.Join("\n", m.Notes?.En ?? new()) : "";
        PublishedAt = r.IsNewer ? m.Published ?? "" : "";
        IsMandatory = r.IsMandatory;
        Progress = 0;
        BytesReceived = 0;
        BytesTotal = r.IsNewer ? Math.Max(m.Windows?.Size ?? 0, 0) : 0;
        ErrorCode = "";
        State = r.IsNewer ? UpdateState.Available : UpdateState.UpToDate;
    }

    private void Skip()
    {
        var version = NewVersion;
        if (!IsMandatory && !string.IsNullOrEmpty(version))
            _store.Update(p => p.SkippedVersion = version);
        RaiseRequestClose();
    }

    private async Task InstallAsync()
    {
        if (_manifest == null || string.IsNullOrEmpty(NewVersion)) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var keepBusy = false;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // macOS: no silent install, open the download page instead.
                if (AutoUpdateService.OpenUrl(AutoUpdateService.MacPageUrl(_manifest)))
                    Ui(RaiseRequestClose);
                else
                    SetError("unsupported_platform");
                return;
            }

            await UiAsync(() =>
            {
                ErrorCode = "";
                Progress = 0;
                BytesReceived = 0;
                BytesTotal = Math.Max(_manifest.Windows?.Size ?? 0, 0);
                State = UpdateState.Downloading;
            }).ConfigureAwait(false);

            var progress = new Progress<(long received, long total)>(p => Ui(() =>
            {
                BytesReceived = p.received;
                if (p.total > 0) BytesTotal = p.total;
                Progress = BytesTotal > 0 ? Math.Min(100.0, p.received * 100.0 / BytesTotal) : 0;
            }));

            var installer = await _service.DownloadAndVerifyAsync(_manifest, progress,
                onVerifying: () => Ui(() => State = UpdateState.Verifying)).ConfigureAwait(false);

            await UiAsync(() => { Progress = 100; State = UpdateState.Installing; }).ConfigureAwait(false);

            // Start the helper first: if that fails the app keeps running and interception stays untouched.
            _service.LaunchInstallHelper(installer);
            keepBusy = true;

            await AutoUpdateService.RunBeforeInstallAsync().ConfigureAwait(false);

            Ui(() =>
            {
                RaiseRequestClose();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Environment.Exit(0);
            });

            // Safety net: the helper waits for this process; make sure it really exits.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Environment.Exit(0);
            });
        }
        catch (Exception ex)
        {
            SetError(ex is UpdateException ue ? ue.Code : "download_failed");
        }
        finally
        {
            if (!keepBusy) Interlocked.Exchange(ref _busy, 0);
        }
    }
}
