using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Win32;
using ProxyBridge.GUI.Controls;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

/// <summary>
/// Main window: HOME (rules table "which apps use which proxy" + connection), PROXIES (saved
/// proxies with real checks) and SETTINGS. The engine is driven per docs/CORE_API.md through
/// <see cref="RulePlanner"/>; on macOS only the "all other apps" row exists.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private static void Log(string msg) => AppLog.Info(msg);

    private IProxyEngine? _engine;
    private Window? _mainWindow;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _latencyTimer;
    private readonly DispatcherTimer _applyTimer;

    // Connection state
    private bool _isConnected;
    private bool _isBusy;
    private bool _autoConnectDone;
    private AppliedRules? _applied;
    private uint _macRuleId;
    private bool _pendingApply;
    private Task<FirewallResult>? _firewallTask;
    private bool _hasFirewallWarning;

    // Live stats (null = unknown, shown as "--")
    private long? _sentBytes;
    private long? _receivedBytes;
    private int? _pingMs;
    private string _latencyTarget = "";

    /// <summary>PROXYBRIDGE_UI_DEMO_CONNECTED=1: connected look with sample numbers, no engine is started.</summary>
    private bool _demo;

    // Navigation
    private AppPage _page = AppPage.Home;

    // Proxies
    private readonly Dictionary<string, string> _proxyLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxyCheckInfo> _proxyChecks = new(StringComparer.Ordinal);
    private string _newProxyText = "";

    // Rules
    private ProxyChoice _defaultChoice = ProxyChoice.Direct;
    private bool _loading;

    // App picker
    private RuleRow? _pickerRow;
    private bool _pickerIsNewRow;
    private bool _isPickerOpen;
    private bool _isPickerLoading;
    private string _pickerQuery = "";
    private List<PickerApp> _pickerAll = new();

    // Settings
    private bool _minimizeToTray = true;
    private bool _startWithWindows;
    private bool _autoConnectLastProxy;
    private bool _showNotifications = true;
    private bool _dnsBypass = true;

    // License
    private LicenseState _license = new();
    private bool _isUnlinking;
    private bool _isUnlinkConfirmVisible;

    public MainWindowViewModel()
    {
        ShowHomeCommand = new RelayCommand(() => ShowPage(AppPage.Home));
        ShowProxiesCommand = new RelayCommand(() => ShowPage(AppPage.Proxies));
        ShowSettingsCommand = new RelayCommand(() => ShowPage(AppPage.Settings));

        MainActionCommand = new RelayCommand(async () => await MainAction());

        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand<RuleRow>(RemoveRule);
        PickAppCommand = new RelayCommand<RuleRow>(r => { if (r != null) OpenPicker(r, isNew: false); });
        PickerSelectCommand = new RelayCommand<PickerApp>(PickerSelect);
        PickerBrowseCommand = new RelayCommand(async () => await PickerBrowse());
        PickerCancelCommand = new RelayCommand(ClosePicker);
        PickerRefreshCommand = new RelayCommand(async () => await LoadPickerApps());

        AddProxyCommand = new RelayCommand(AddProxiesFromInput);
        ImportProxiesCommand = new RelayCommand(async () => await ImportProxies());
        CheckProxyCommand = new RelayCommand<ProxyItem>(async p => await CheckProxy(p));
        CheckAllCommand = new RelayCommand(async () => await CheckAll());
        DeleteProxyCommand = new RelayCommand<ProxyItem>(DeleteProxy);
        StartRenameCommand = new RelayCommand<ProxyItem>(StartRename);
        CommitRenameCommand = new RelayCommand<ProxyItem>(CommitRename);
        CancelRenameCommand = new RelayCommand<ProxyItem>(e => { if (e != null) e.IsEditing = false; });

        CheckUpdatesCommand = new RelayCommand(() => (Avalonia.Application.Current as App)?.ShowUpdateWindow(checkNow: true));
        OpenLinkCommand = new RelayCommand<string>(OpenUrl);
        OpenTelegramCommand = new RelayCommand(() => OpenUrl("https://t.me/inter1ark"));
        OpenLogsCommand = new RelayCommand(OpenLogsFolder);
        UnlinkDeviceCommand = new RelayCommand(async () => await UnlinkDevice());
        AskUnlinkCommand = new RelayCommand(() => IsUnlinkConfirmVisible = true);
        CancelUnlinkCommand = new RelayCommand(() => IsUnlinkConfirmVisible = false);

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += UpdateStats;
        _latencyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _latencyTimer.Tick += async (_, _) => await MeasureLatencyAsync();
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            await ApplyLive();
        };

        Rules.CollectionChanged += (_, _) => OnRulesShapeChanged();
        Proxies.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProxies));
            OnPropertyChanged(nameof(HasNoProxies));
            OnPropertyChanged(nameof(ProxiesCountText));
        };
        Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocStatus.Kind)) RaisePowerState();
        };

        ProxyChoices.Add(ProxyChoice.Direct);
        LoadSettings();
        RefreshLicenseInfo();
        SetIdleStatus();

        I18n.Instance.LanguageChanged += OnLanguageChanged;
    }

    // ====================================================================
    // Commands
    // ====================================================================
    public ICommand ShowHomeCommand { get; }
    public ICommand ShowProxiesCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand MainActionCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand PickAppCommand { get; }
    public ICommand PickerSelectCommand { get; }
    public ICommand PickerBrowseCommand { get; }
    public ICommand PickerCancelCommand { get; }
    public ICommand PickerRefreshCommand { get; }
    public ICommand AddProxyCommand { get; }
    public ICommand ImportProxiesCommand { get; }
    public ICommand CheckProxyCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand DeleteProxyCommand { get; }
    public ICommand StartRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand OpenTelegramCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand UnlinkDeviceCommand { get; }
    public ICommand AskUnlinkCommand { get; }
    public ICommand CancelUnlinkCommand { get; }

    // ====================================================================
    // Status messages (localized, re-rendered on language change)
    // ====================================================================
    /// <summary>Connection status (HOME right panel).</summary>
    public LocStatus Status { get; } = new();
    /// <summary>Rules table status line (HOME, under the table).</summary>
    public LocStatus RulesStatus { get; } = new();
    /// <summary>Proxies tab status line.</summary>
    public LocStatus ProxiesStatus { get; } = new();

    private void SetIdleStatus()
    {
        if (IsConnected || _isBusy) return;
        Status.Clear();
    }

    private static readonly string[] LocalizedProperties =
    {
        nameof(ConnectButtonLabel), nameof(Received), nameof(Sent), nameof(Latency),
        nameof(LicensePlanText), nameof(LicenseExpiresText), nameof(LicenseFooterText), nameof(StatusWord),
        nameof(LanguageIndex), nameof(Summary), nameof(ProxiesCountText), nameof(ExitPlace)
    };

    private void OnLanguageChanged()
    {
        Status.Refresh();
        RulesStatus.Refresh();
        ProxiesStatus.Refresh();
        foreach (var p in Proxies) p.RefreshTexts();
        foreach (var name in LocalizedProperties) OnPropertyChanged(name);
    }

    // ====================================================================
    // Navigation
    // ====================================================================
    public AppPage Page
    {
        get => _page;
        private set
        {
            if (!SetProperty(ref _page, value)) return;
            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsProxiesPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    public bool IsHomePage => _page == AppPage.Home;
    public bool IsProxiesPage => _page == AppPage.Proxies;
    public bool IsSettingsPage => _page == AppPage.Settings;

    private void ShowPage(AppPage page)
    {
        if (page != AppPage.Settings) IsUnlinkConfirmVisible = false;
        if (page != AppPage.Home) ClosePicker();
        foreach (var p in Proxies) p.IsEditing = false;
        Page = page;
    }

    // ====================================================================
    // Platform
    // ====================================================================
    /// <summary>Per-app rules are available (Windows engine). On macOS only "all other apps" exists.</summary>
    public bool SupportsSplitTunnel => _engine?.SupportsSplitTunnel ?? !OperatingSystem.IsMacOS();
    public bool IsWindows => OperatingSystem.IsWindows();
    public string AppVersion => LicenseService.AppVersion;

    // ====================================================================
    // Connection state
    // ====================================================================
    public bool IsConnected => _isConnected;
    public bool IsConnecting => _isBusy;
    public bool IsDisconnected => !_isConnected && !_isBusy;
    public bool CanToggle => !_isBusy;

    private void SetConnected(bool value)
    {
        if (_isConnected == value) return;
        _isConnected = value;
        RaiseConnectionStateChanged();
    }

    private void SetBusy(bool value)
    {
        if (_isBusy == value) return;
        _isBusy = value;
        RaiseConnectionStateChanged();
    }

    private void RaiseConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ConnectButtonLabel));
        RaisePowerState();
    }

    public bool IsErrorState => !IsConnected && !_isBusy && Status.IsError;

    public PowerState PowerState =>
        _isBusy ? PowerState.Connecting
        : IsConnected ? PowerState.Connected
        : Status.IsError ? PowerState.Error
        : PowerState.Disconnected;

    /// <summary>ПОДКЛЮЧЕНО / ОТКЛЮЧЕНО / ПОДКЛЮЧЕНИЕ... / ОШИБКА under the dial.</summary>
    public string StatusWord => I18n.T(
        _isBusy ? "ov.state.connecting"
        : IsConnected ? "ov.state.connected"
        : Status.IsError ? "ov.state.error"
        : "ov.state.disconnected");

    private void RaisePowerState()
    {
        OnPropertyChanged(nameof(PowerState));
        OnPropertyChanged(nameof(StatusWord));
        OnPropertyChanged(nameof(IsErrorState));
    }

    public string ConnectButtonLabel => I18n.T(IsConnected ? "conn.disconnect" : "conn.connect");

    private async Task MainAction()
    {
        if (_isBusy) return;
        ClosePicker();
        if (_demo)
        {
            // preview mode never touches the engine; validation still runs
            if (IsConnected) { SetConnected(false); _pingMs = null; ResetStats(); Status.Clear(); }
            else if (BuildPlan(Status) != null) { SetConnected(true); Status.Clear(); ApplyDemoNumbers(); }
            return;
        }
        if (IsConnected)
        {
            await Disconnect();
            return;
        }
        await Connect();
    }

    // ====================================================================
    // Proxies
    // ====================================================================
    public ObservableCollection<ProxyItem> Proxies { get; } = new();

    /// <summary>"Direct" plus every saved proxy, for the proxy pickers on HOME.</summary>
    public ObservableCollection<ProxyChoice> ProxyChoices { get; } = new();

    public bool HasProxies => Proxies.Count > 0;
    public bool HasNoProxies => Proxies.Count == 0;
    public string ProxiesCountText => Proxies.Count.ToString(I18n.Instance.Culture);

    public string NewProxyText
    {
        get => _newProxyText;
        set => SetProperty(ref _newProxyText, value);
    }

    private ProxyItem? FindProxy(string raw) => Proxies.FirstOrDefault(p => p.Raw == raw);

    private ProxyChoice ChoiceFor(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return ProxyChoice.Direct;
        return ProxyChoices.FirstOrDefault(c => c.Raw == raw) ?? ProxyChoice.Direct;
    }

    private ProxyItem CreateItem(string raw, ParsedProxy parsed)
    {
        var item = new ProxyItem(raw, parsed);
        if (_proxyLabels.TryGetValue(raw, out var l)) item.Label = l;
        if (_proxyChecks.TryGetValue(raw, out var c)) item.Check = c;
        return item;
    }

    private void InsertProxy(ProxyItem item, int index = -1)
    {
        if (index < 0 || index > Proxies.Count) index = Proxies.Count;
        Proxies.Insert(index, item);
        ProxyChoices.Insert(index + 1, new ProxyChoice(item));
    }

    /// <summary>Adds every valid line of the paste field (one or many proxies).</summary>
    private void AddProxiesFromInput()
    {
        var text = (NewProxyText ?? "").Trim();
        if (text.Length == 0)
        {
            ProxiesStatus.Set("status.enter_proxy", StatusKind.Warning);
            return;
        }
        var (added, skipped, dup) = AddLines(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (added.Count == 0)
        {
            ProxiesStatus.Set(dup > 0 && skipped == 0 ? "status.already_saved" : "status.invalid_format",
                dup > 0 && skipped == 0 ? StatusKind.Neutral : StatusKind.Warning);
            return;
        }
        NewProxyText = "";
        ReportAdded(added.Count, skipped, imported: false);
        _ = CheckMany(added);
    }

    /// <summary>Adds proxy lines; returns the new items, invalid line count and duplicate count.</summary>
    private (List<ProxyItem> added, int skipped, int dup) AddLines(IEnumerable<string> lines)
    {
        var added = new List<ProxyItem>();
        int skipped = 0, dup = 0;
        var wasEmpty = Proxies.Count == 0;
        foreach (var line0 in lines)
        {
            var line = line0.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (!ProxyParser.TryParse(line, out var parsed)) { skipped++; continue; }
            if (FindProxy(line) != null) { dup++; continue; }
            var item = CreateItem(line, parsed);
            InsertProxy(item);
            added.Add(item);
        }
        if (added.Count > 0)
        {
            // first proxy ever and no rules yet: make it the target of "all other apps"
            if (wasEmpty && Rules.Count == 0 && _defaultChoice.IsDirect)
                SetDefaultChoice(ChoiceFor(added[0].Raw), apply: true);
            SaveSettings();
        }
        return (added, skipped, dup);
    }

    private void ReportAdded(int added, int skipped, bool imported)
    {
        var kind = added > 0 ? StatusKind.Success : StatusKind.Warning;
        if (skipped > 0)
            ProxiesStatus.Set(imported ? "proxies.imported_skipped" : "proxies.added_skipped", kind, added, skipped);
        else if (!imported && added == 1)
            ProxiesStatus.Set("status.saved", kind);
        else
            ProxiesStatus.Set(imported ? "proxies.imported" : "proxies.added_n", kind, added);
    }

    private async Task ImportProxies()
    {
        if (_mainWindow == null) return;
        try
        {
            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = I18n.T("proxies.import_title"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(I18n.T("file.text")) { Patterns = new[] { "*.txt", "*.csv" } },
                    new FilePickerFileType(I18n.T("file.all")) { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            var (added, skipped, _) = AddLines(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            ReportAdded(added.Count, skipped, imported: true);
            if (added.Count <= 20) _ = CheckMany(added);
        }
        catch (Exception ex)
        {
            AppLog.Error("Import failed", ex);
            ProxiesStatus.Set("proxies.import_error", StatusKind.Error, ex.Message);
        }
    }

    private void DeleteProxy(ProxyItem? item)
    {
        if (item == null) return;
        var index = Proxies.IndexOf(item);
        if (index < 0) return;

        var usedByRules = false;
        _loading = true;
        try
        {
            foreach (var row in Rules)
            {
                if (row.Mapping.ProxyString == item.Raw)
                {
                    row.SetChoiceSilently(ProxyChoice.Direct);
                    usedByRules = true;
                }
            }
            if (_defaultChoice.Raw == item.Raw)
            {
                SetDefaultChoice(ProxyChoice.Direct, apply: false);
                usedByRules = true;
            }
            var choice = ProxyChoices.FirstOrDefault(c => ReferenceEquals(c.Item, item));
            if (choice != null) ProxyChoices.Remove(choice);
            Proxies.RemoveAt(index);
            _proxyLabels.Remove(item.Raw);
            _proxyChecks.Remove(item.Raw);
        }
        finally
        {
            _loading = false;
        }
        SaveSettings();
        ProxiesStatus.Set(usedByRules ? "proxies.deleted_rules" : "proxies.deleted", StatusKind.Neutral, item.Name);
        OnRulesChanged();
    }

    private void StartRename(ProxyItem? item)
    {
        if (item == null) return;
        foreach (var p in Proxies) if (!ReferenceEquals(p, item)) p.IsEditing = false;
        item.EditLabel = item.Label.Length > 0 ? item.Label : item.Name;
        item.IsEditing = true;
    }

    private void CommitRename(ProxyItem? item)
    {
        if (item == null || !item.IsEditing) return;
        item.IsEditing = false;
        var label = (item.EditLabel ?? "").Trim();
        if (label.Length > 40) label = label.Substring(0, 40);
        if (label == item.Parsed.Host) label = "";
        item.Label = label;
        if (label.Length == 0) _proxyLabels.Remove(item.Raw);
        else _proxyLabels[item.Raw] = label;
        SaveSettings();
        OnPropertyChanged(nameof(Summary));
    }

    // ---------------- checks ----------------
    public bool IsCheckingAll { get => _isCheckingAll; private set => SetProperty(ref _isCheckingAll, value); }
    private bool _isCheckingAll;

    private async Task CheckProxy(ProxyItem? item)
    {
        if (item == null || item.IsChecking) return;
        item.IsChecking = true;
        try
        {
            var result = await ProxyChecker.CheckAsync(item.Parsed, I18n.Instance.Language);
            Log($"Check {item.Type} {item.HostPort}: {(result.Ok ? $"ok ip={result.Ip} {result.CountryCode} {result.City} {result.LatencyMs} ms" : $"{result.Error} ({result.Detail})")}");
            var info = result.ToInfo();
            _proxyChecks[item.Raw] = info;
            item.Check = info;
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Proxy check failed", ex);
        }
        finally
        {
            item.IsChecking = false;
        }
        // after IsChecking is cleared, so IsOk is already true for the HOME exit line
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ExitPlace));
        OnPropertyChanged(nameof(ExitCountryCode));
        OnPropertyChanged(nameof(HasExitInfo));
    }

    private async Task CheckMany(IReadOnlyCollection<ProxyItem> items)
    {
        if (items.Count == 0) return;
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async p =>
        {
            await gate.WaitAsync();
            try { await CheckProxy(p); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task CheckAll()
    {
        if (IsCheckingAll || Proxies.Count == 0) return;
        IsCheckingAll = true;
        try
        {
            ProxiesStatus.Set("proxies.checking_all", StatusKind.Neutral);
            await CheckMany(Proxies.ToList());
            var ok = Proxies.Count(p => p.IsOk);
            ProxiesStatus.Set("proxies.checked_all", ok == Proxies.Count ? StatusKind.Success : StatusKind.Warning, ok, Proxies.Count);
        }
        finally
        {
            IsCheckingAll = false;
        }
    }

    // ====================================================================
    // Rules table (HOME)
    // ====================================================================
    public ObservableCollection<RuleRow> Rules { get; } = new();

    /// <summary>The firewall allow rule could not be ensured: HOME shows a non-blocking hint.</summary>
    public bool HasFirewallWarning
    {
        get => _hasFirewallWarning;
        private set => SetProperty(ref _hasFirewallWarning, value);
    }

    public bool HasRules => Rules.Count > 0;
    public bool HasNoRules => Rules.Count == 0;
    public bool ShowNoRulesHint => SupportsSplitTunnel && Rules.Count == 0;

    /// <summary>Target of "all other apps": a saved proxy or Direct.</summary>
    public ProxyChoice? DefaultChoice
    {
        get => _defaultChoice;
        set
        {
            if (value == null || ReferenceEquals(value, _defaultChoice)) return;
            SetDefaultChoice(value, apply: true);
        }
    }

    private void SetDefaultChoice(ProxyChoice choice, bool apply)
    {
        _defaultChoice = choice;
        OnPropertyChanged(nameof(DefaultChoice));
        if (apply && !_loading)
        {
            SaveSettings();
            OnRulesChanged();
        }
    }

    private RuleRow CreateRow(AppProxyMapping m)
    {
        var row = new RuleRow(m, ProxyChoices);
        row.SetChoiceSilently(ChoiceFor(m.ProxyString));
        row.Changed += OnRowChanged;
        return row;
    }

    private void OnRowChanged(RuleRow row)
    {
        if (_loading) return;
        SaveSettings();
        OnRulesChanged();
    }

    private void OnRulesShapeChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(ShowNoRulesHint));
    }

    /// <summary>Something in the table changed: refresh the summary and apply live when connected.</summary>
    private void OnRulesChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ExitPlace));
        OnPropertyChanged(nameof(ExitCountryCode));
        OnPropertyChanged(nameof(HasExitInfo));
        if (!IsConnected && !_isBusy)
        {
            if (Status.IsError || Status.IsWarning) Status.Clear();
            _ = MeasureLatencyAsync();
        }
        if ((IsConnected || _isBusy) && !_demo)
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        }
    }

    private void AddRule()
    {
        if (!SupportsSplitTunnel) return;
        var row = CreateRow(new AppProxyMapping { ProxyString = NewRowProxy() });
        Rules.Add(row);
        OpenPicker(row, isNew: true);
    }

    /// <summary>A sensible proxy for a new row: the first saved proxy that "all other apps" do not use already, else Direct.</summary>
    private string NewRowProxy() => Proxies.FirstOrDefault(p => p.Raw != _defaultChoice.Raw)?.Raw ?? "";

    private void RemoveRule(RuleRow? row)
    {
        if (row == null) return;
        row.Changed -= OnRowChanged;
        Rules.Remove(row);
        if (ReferenceEquals(_pickerRow, row)) ClosePicker();
        SaveSettings();
        if (row.HasApp)
        {
            RulesStatus.Set("home.removed", StatusKind.Neutral, row.DisplayName);
            OnRulesChanged();
        }
    }

    /// <summary>"Chrome и Telegram через Amsterdam, остальное напрямую".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var groups = new List<(string target, List<string> names)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (SupportsSplitTunnel)
            {
                foreach (var r in Rules)
                {
                    if (!r.Enabled || !r.HasApp || !seen.Add(r.ProcessName)) continue;
                    var target = r.Mapping.ProxyString;
                    var name = r.DisplayName.Length > 22 ? r.DisplayName.Substring(0, 21) + "…" : r.DisplayName;
                    var g = groups.FindIndex(x => x.target == target);
                    if (g < 0) groups.Add((target, new List<string> { name }));
                    else groups[g].names.Add(name);
                }
            }
            foreach (var (target, names) in groups)
            {
                var who = JoinNames(names);
                parts.Add(target.Length == 0
                    ? I18n.T("home.sum.direct", who)
                    : I18n.T("home.sum.via", who, ProxyName(target)));
            }

            var def = _defaultChoice.Raw;
            if (groups.Count == 0)
            {
                return def.Length == 0
                    ? I18n.T("home.sum.nothing")
                    : I18n.T("home.sum.all_via", ProxyName(def));
            }
            parts.Add(def.Length == 0 ? I18n.T("home.sum.rest_direct") : I18n.T("home.sum.rest_via", ProxyName(def)));
            var text = string.Join(", ", parts);
            return text.Length > 0 ? char.ToUpper(text[0], I18n.Instance.Culture) + text.Substring(1) : text;
        }
    }

    private string ProxyName(string raw) => FindProxy(raw)?.Name ?? (ProxyParser.TryParse(raw, out var p) ? p.Host : "?");

    private static string JoinNames(List<string> names)
    {
        var and = I18n.T("home.sum.and");
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return $"{names[0]} {and} {names[1]}";
        if (names.Count == 3) return $"{names[0]}, {names[1]} {and} {names[2]}";
        return I18n.T("home.sum.more", $"{names[0]}, {names[1]}", names.Count - 2);
    }

    /// <summary>The proxy whose exit is shown on HOME and probed for latency: "all other apps", else the first active rule's proxy.</summary>
    private string MainProxyRaw()
    {
        if (_defaultChoice.IsProxy) return _defaultChoice.Raw;
        if (SupportsSplitTunnel)
            foreach (var r in Rules)
                if (r.Enabled && r.HasApp && r.Mapping.ProxyString.Length > 0) return r.Mapping.ProxyString;
        return "";
    }

    public bool HasExitInfo => FindProxy(MainProxyRaw()) is { IsOk: true };
    public string ExitCountryCode => FindProxy(MainProxyRaw())?.CountryCode ?? "";

    /// <summary>"Германия, Франкфурт  ·  185.1.2.3" of the main proxy after a check.</summary>
    public string ExitPlace
    {
        get
        {
            var p = FindProxy(MainProxyRaw());
            if (p?.Check is not { Ok: true } c) return "";
            return p.Place.Length > 0 ? $"{p.Place}  ·  {c.Ip}" : c.Ip;
        }
    }

    // ---------------- app picker ----------------
    public bool IsPickerOpen { get => _isPickerOpen; private set => SetProperty(ref _isPickerOpen, value); }
    public bool IsPickerLoading { get => _isPickerLoading; private set => SetProperty(ref _isPickerLoading, value); }
    public ObservableCollection<PickerApp> PickerApps { get; } = new();
    public bool PickerEmpty => !_isPickerLoading && PickerApps.Count == 0;

    public string PickerQuery
    {
        get => _pickerQuery;
        set
        {
            if (SetProperty(ref _pickerQuery, value ?? "")) FilterPicker();
        }
    }

    private void OpenPicker(RuleRow row, bool isNew)
    {
        if (!SupportsSplitTunnel) return;
        _pickerRow = row;
        _pickerIsNewRow = isNew;
        _pickerQuery = "";
        OnPropertyChanged(nameof(PickerQuery));
        IsPickerOpen = true;
        _ = LoadPickerApps();
    }

    private void ClosePicker()
    {
        if (!_isPickerOpen && _pickerRow == null) return;
        var row = _pickerRow;
        _pickerRow = null;
        IsPickerOpen = false;
        // a new row that never got a program is dropped
        if (row != null && _pickerIsNewRow && !row.HasApp)
        {
            row.Changed -= OnRowChanged;
            Rules.Remove(row);
        }
        _pickerIsNewRow = false;
    }

    private async Task LoadPickerApps()
    {
        if (IsPickerLoading) return;
        IsPickerLoading = true;
        OnPropertyChanged(nameof(PickerEmpty));
        try
        {
            var list = await Task.Run(ProcessCatalog.List);
            _pickerAll = list.Select(p => new PickerApp(p)).ToList();
            foreach (var app in _pickerAll)
            {
                var a = app;
                AppIconService.Request(a.ExeName, a.Path, bmp => a.Icon = bmp);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Process list failed", ex);
            _pickerAll = new List<PickerApp>();
        }
        finally
        {
            IsPickerLoading = false;
        }
        FilterPicker();
    }

    private void FilterPicker()
    {
        var q = _pickerQuery.Trim();
        PickerApps.Clear();
        foreach (var a in _pickerAll)
            if (a.Matches(q)) PickerApps.Add(a);
        OnPropertyChanged(nameof(PickerEmpty));
    }

    private void PickerSelect(PickerApp? app)
    {
        if (app == null) return;
        ApplyProgram(app.ExeName, app.Path);
    }

    private async Task PickerBrowse()
    {
        if (_mainWindow == null || _pickerRow == null) return;
        try
        {
            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = I18n.T("apps.browse_title"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(I18n.T("file.programs")) { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType(I18n.T("file.all")) { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? "";
            ApplyProgram(files[0].Name, path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Browse for program failed", ex);
            RulesStatus.Set("apps.browse_error", StatusKind.Error, ex.Message);
        }
    }

    private void ApplyProgram(string exeName, string path)
    {
        var row = _pickerRow;
        if (row == null) return;
        exeName = (exeName ?? "").Trim();
        if (exeName.Length == 0) return;

        var other = Rules.FirstOrDefault(r => !ReferenceEquals(r, row) &&
            string.Equals(r.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));
        if (other != null)
        {
            RulesStatus.Set("home.duplicate", StatusKind.Warning, RuleRow.Pretty(exeName));
            ClosePicker();
            return;
        }

        _pickerIsNewRow = false;
        _pickerRow = null;
        IsPickerOpen = false;
        AppIconService.Invalidate(exeName);
        row.SetProgram(exeName, path);
        RulesStatus.Set("home.added", StatusKind.Success, row.DisplayName);
    }

    // ====================================================================
    // Engine
    // ====================================================================
    public void SetMainWindow(Window window) => _mainWindow = window;

    public void Initialize(IProxyEngine engine)
    {
        _engine = engine;
        _engine.LogReceived += AppLog.Core;
        // Engine stopped on its own (macOS helper exited): reset the UI state.
        _engine.Stopped += msg => Dispatcher.UIThread.Post(() => OnEngineStopped(msg));

        OnPropertyChanged(nameof(SupportsSplitTunnel));
        OnPropertyChanged(nameof(ShowNoRulesHint));
        OnPropertyChanged(nameof(Summary));
        Log($"ProxyBridge {AppVersion} started on {(OperatingSystem.IsMacOS() ? "macOS" : "Windows")}; split tunnel: {SupportsSplitTunnel}; rules: {Rules.Count}; proxies: {Proxies.Count}");

        // Windows Firewall: the core receives redirected connections as inbound TCP, so make sure
        // ProxyBridge.exe is allowed (elevated runs only). Connect waits for this.
        if (OperatingSystem.IsWindows() && _engine.SupportsSplitTunnel)
        {
            _firewallTask = Task.Run(FirewallService.EnsureForCurrentExe);
            _firewallTask.ContinueWith(t =>
            {
                if (t.Result.Outcome == FirewallOutcome.Failed)
                    Dispatcher.UIThread.Post(() => HasFirewallWarning = true);
            }, TaskScheduler.Default);
        }

        // UI preview for screenshots: connected look with sample numbers, the engine is never started.
        if (Environment.GetEnvironmentVariable("PROXYBRIDGE_UI_DEMO_CONNECTED") == "1")
        {
            _demo = true;
            _autoConnectDone = true;
            SetConnected(true);
            ApplyDemoNumbers();
        }
        else
        {
            _ = MeasureLatencyAsync();
        }
    }

    private void OnEngineStopped(string message)
    {
        if (!_isConnected) return;
        _applied = null;
        SetConnected(false);
        if (string.IsNullOrWhiteSpace(message))
            Status.Set("status.stopped_unexpected", StatusKind.Error);
        else
            Status.Set("status.stopped_with_reason", StatusKind.Error, message);
        AppLog.Warn($"Engine stopped on its own: {message}");
        StopTimers();
    }

    /// <summary>Validates the table and returns the plan, or sets the error status and returns null.</summary>
    private RulePlan? BuildPlan(LocStatus target)
    {
        if (Proxies.Count == 0)
        {
            target.Set("home.err.no_saved_proxies", StatusKind.Warning);
            return null;
        }
        var rows = SupportsSplitTunnel
            ? Rules.Select(r => new RuleInput(r.ProcessName, r.Mapping.ProxyString, r.Enabled))
            : Enumerable.Empty<RuleInput>();
        var plan = RulePlanner.Build(rows, _defaultChoice.Raw);
        if (!plan.IsValid)
        {
            target.Set(plan.ErrorKey, StatusKind.Warning, plan.ErrorArg);
            return null;
        }
        return plan;
    }

    private async Task Connect()
    {
        if (_engine == null)
        {
            Status.Set("status.service_not_ready", StatusKind.Error);
            return;
        }
        var plan = BuildPlan(Status);
        if (plan == null) return;

        SetBusy(true);
        try
        {
            await Task.Delay(50); // let the "connecting" state render
            if (_firewallTask != null)
            {
                try { await _firewallTask; } catch { /* logged by the service */ }
            }
            var ok = await StartEngine(plan);
            if (ok)
            {
                SetConnected(true);
                Status.Clear(); // the status word says it all
                StartTimers();
            }
        }
        finally
        {
            SetBusy(false);
        }
        if (_pendingApply && IsConnected)
        {
            _pendingApply = false;
            await ApplyLive();
        }
    }

    /// <summary>Starts the engine with a plan. On failure sets the status, leaves the engine stopped and returns false.</summary>
    private async Task<bool> StartEngine(RulePlan plan)
    {
        var engine = _engine!;
        try
        {
            Log($"Connect: {plan.Proxies.Count} proxies, {plan.Rules.Count} rules, catch-all {(plan.DefaultProxy.Length > 0 ? "proxy" : "direct")}");
            foreach (var r in plan.Rules)
                Log($"  rule {r.Process} -> {(r.IsDirect ? "DIRECT" : AppLog.Sanitize(r.Proxy))}");

            if (!SupportsSplitTunnel)
                return await StartMac(engine, plan);

            // Optional legacy switches: the core proxies TCP only and sends DNS direct (docs/CORE_API.md),
            // so a core without these exports is fine.
            Optional(() => engine.SetDisableUdp(true));
            Optional(() => engine.SetDnsViaProxy(false));
            var applied = RulePlanner.RegisterProxies(engine, plan);
            if (!applied.Ok)
            {
                Status.Set(applied.ErrorKey, StatusKind.Error, applied.ErrorArg);
                AppLog.Error($"AddProxy failed for {applied.ErrorArg}");
                engine.ClearProxies();
                return false;
            }

            if (!await engine.StartAsync())
            {
                AppLog.Error($"Start failed: {engine.LastError}");
                Status.Set(string.IsNullOrEmpty(engine.LastError) ? "status.start_failed_admin" : "status.error_detail",
                    StatusKind.Error, engine.LastError);
                engine.ClearProxies();
                return false;
            }

            RulePlanner.AddRules(engine, plan, applied);
            _applied = applied;
            if (!applied.Ok)
            {
                AppLog.Error($"AddRuleEx failed for {applied.ErrorArg}");
                StopEngine();
                Status.Set(applied.ErrorKey, StatusKind.Error, applied.ErrorArg);
                return false;
            }
            Log($"Connected: proxy ids [{string.Join(", ", applied.ProxyIds.Values)}], rule ids [{string.Join(", ", applied.RuleIds)}]");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Connect failed", ex);
            Status.Set("status.error_detail", StatusKind.Error, ex.Message);
            try { StopEngine(); } catch { }
            return false;
        }
    }

    private static void Optional(Action call)
    {
        try { call(); }
        catch (EntryPointNotFoundException ex) { AppLog.Info($"Optional core export missing: {ex.Message}"); }
    }

    /// <summary>macOS: one proxy for the whole computer (pbcore).</summary>
    private async Task<bool> StartMac(IProxyEngine engine, RulePlan plan)
    {
        ProxyParser.TryParse(plan.DefaultProxy, out var p);
        if (!engine.SetProxyConfig(p.Type, p.Host, ushort.Parse(p.Port), p.User, p.Pass))
        {
            Status.Set("status.configure_failed", StatusKind.Error, $"{p.Type} {p.HostPort}");
            return false;
        }
        engine.SetDisableUdp(true);
        if (!await engine.StartAsync())
        {
            AppLog.Error($"Start failed: {engine.LastError}");
            Status.Set(string.IsNullOrEmpty(engine.LastError) ? "status.start_failed_admin" : "status.error_detail",
                StatusKind.Error, engine.LastError);
            return false;
        }
        _macRuleId = engine.AddRule("*", "*", "*", "BOTH", "PROXY");
        return true;
    }

    /// <summary>Removes the rules and proxies and stops the engine. Safe to call twice.</summary>
    private void StopEngine()
    {
        var engine = _engine;
        if (engine == null) return;
        try
        {
            if (_applied != null)
                foreach (var id in _applied.RuleIds)
                    try { engine.DeleteRule(id); } catch { }
            if (_macRuleId > 0)
            {
                try { engine.DeleteRule(_macRuleId); } catch { }
                _macRuleId = 0;
            }
            _applied = null;
            try { engine.ClearProxies(); } catch { }
            engine.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Error("Stop failed", ex);
        }
    }

    private Task Disconnect()
    {
        _applyTimer.Stop();
        _pendingApply = false;
        if (_demo)
        {
            SetConnected(false);
            StopTimers();
            return Task.CompletedTask;
        }
        StopEngine();
        SetConnected(false);
        Status.Clear();
        StopTimers();
        Log("Disconnected");
        return Task.CompletedTask;
    }

    /// <summary>Connected and the table changed: restart the engine with the new rules.</summary>
    private async Task ApplyLive()
    {
        if (_demo || !IsConnected) return;
        if (_isBusy)
        {
            _pendingApply = true;
            return;
        }
        Log("Rules changed while connected: restarting the engine");
        var plan = BuildPlan(Status);
        SetBusy(true);
        try
        {
            StopEngine();
            if (plan == null)
            {
                // nothing to route through a proxy any more: stay disconnected, the warning explains why
                SetConnected(false);
                StopTimers();
                return;
            }
            await Task.Delay(150);
            if (await StartEngine(plan))
            {
                Status.Set("status.applied", StatusKind.Success);
                _sentBytes = 0;
                _receivedBytes = 0;
                RaiseStats();
                _ = MeasureLatencyAsync();
            }
            else
            {
                SetConnected(false);
                StopTimers();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void Cleanup()
    {
        I18n.Instance.LanguageChanged -= OnLanguageChanged;
        SaveSettings();
        _statsTimer.Stop();
        _latencyTimer.Stop();
        _applyTimer.Stop();
        if (!_demo && (IsConnected || _applied != null)) StopEngine();
        try { _engine?.Stop(); } catch { }
        Log("Exit");
    }

    /// <summary>Stops the connection (used before an update installs).</summary>
    public async Task StopAllConnectionsAsync()
    {
        if (IsConnected) await Disconnect();
    }

    public async Task AutoConnectIfNeeded()
    {
        if (_autoConnectDone) return;
        _autoConnectDone = true;
        if (!_autoConnectLastProxy || _engine == null || IsConnected) return;
        var plan = BuildPlan(new LocStatus());
        if (plan == null) return;
        Log("Auto-connect at startup");
        await Connect();
    }

    // ====================================================================
    // Stats (real numbers only)
    // ====================================================================
    public string Received => FormatBytes(_receivedBytes);
    public string Sent => FormatBytes(_sentBytes);
    public string Latency => _pingMs.HasValue ? I18n.T("unit.ms", _pingMs.Value) : "--";

    /// <summary>1 023 Б, 12,4 КБ, 86 МБ, 1,24 ГБ.</summary>
    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return "--";
        double v = bytes.Value;
        string unit;
        if (v < 1024) return I18n.T("unit.b", (long)v);
        if (v < 1024 * 1024) { v /= 1024; unit = "unit.kb"; }
        else if (v < 1024L * 1024 * 1024) { v /= 1024 * 1024; unit = "unit.mb"; }
        else { v /= 1024.0 * 1024 * 1024; unit = "unit.gb"; }
        var fmt = v < 10 ? "0.##" : v < 100 ? "0.#" : "0";
        return I18n.T(unit, v.ToString(fmt, I18n.Instance.Culture));
    }

    private void ResetStats()
    {
        _sentBytes = null;
        _receivedBytes = null;
        RaiseStats();
    }

    private void RaiseStats()
    {
        OnPropertyChanged(nameof(Received));
        OnPropertyChanged(nameof(Sent));
        OnPropertyChanged(nameof(Latency));
    }

    private void UpdateStats(object? sender, EventArgs e)
    {
        if (_demo || _engine == null || !_engine.ProvidesTrafficStats) return;
        if (_engine.TryGetTrafficStats(out var up, out var down))
        {
            _sentBytes = Math.Max(0, up);
            _receivedBytes = Math.Max(0, down);
            OnPropertyChanged(nameof(Received));
            OnPropertyChanged(nameof(Sent));
        }
    }

    private bool _measuring;

    private async Task MeasureLatencyAsync()
    {
        if (_demo || _measuring) return;
        var raw = MainProxyRaw();
        if (raw != _latencyTarget)
        {
            _latencyTarget = raw;
            _pingMs = null;
            OnPropertyChanged(nameof(Latency));
        }
        if (raw.Length == 0 || !ProxyParser.TryParse(raw, out var p) || !int.TryParse(p.Port, out var port)) return;
        _measuring = true;
        try
        {
            var ms = await LatencyProbe.MeasureAsync(p.Host, port);
            if (raw == _latencyTarget)
            {
                _pingMs = ms;
                OnPropertyChanged(nameof(Latency));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Latency probe failed: {ex.Message}");
        }
        finally
        {
            _measuring = false;
        }
    }

    private void StartTimers()
    {
        _sentBytes = null;
        _receivedBytes = null;
        if (_engine?.ProvidesTrafficStats == true)
        {
            _sentBytes = 0;
            _receivedBytes = 0;
            _statsTimer.Start();
        }
        _latencyTimer.Start();
        RaiseStats();
        _ = MeasureLatencyAsync();
    }

    private void StopTimers()
    {
        _statsTimer.Stop();
        _latencyTimer.Stop();
        ResetStats();
    }

    private void ApplyDemoNumbers()
    {
        _pingMs = 42;
        _receivedBytes = (long)(1.24 * 1024 * 1024 * 1024);
        _sentBytes = 86L * 1024 * 1024;
        RaiseStats();
    }

    // ====================================================================
    // Settings
    // ====================================================================
    /// <summary>0 = Русский, 1 = English.</summary>
    public int LanguageIndex
    {
        get => I18n.Instance.IsRussian ? 0 : 1;
        set
        {
            var lang = value == 0 ? I18n.Russian : I18n.English;
            if (lang == I18n.Instance.Language) return;
            I18n.Instance.SetLanguageAndSave(lang);
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { if (SetProperty(ref _minimizeToTray, value)) SaveSettings(); }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (SetProperty(ref _startWithWindows, value)) { UpdateAutoStart(value); SaveSettings(); } }
    }

    public bool AutoConnectLastProxy
    {
        get => _autoConnectLastProxy;
        set { if (SetProperty(ref _autoConnectLastProxy, value)) SaveSettings(); }
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set { if (SetProperty(ref _showNotifications, value)) SaveSettings(); }
    }

    public bool DnsBypass
    {
        get => _dnsBypass;
        set
        {
            if (!SetProperty(ref _dnsBypass, value)) return;
            SaveSettings();
            OnRulesChanged();
        }
    }

    public string LogsFolder => AppLog.Directory;

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Directory);
            AppLog.Info("Logs folder opened");
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{AppLog.Directory}\"", UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{AppLog.Directory}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open logs folder failed", ex);
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var config = ConfigManager.LoadConfig();
            _minimizeToTray = config.CloseToTray;
            _startWithWindows = IsAutoStartEnabled(); // the registry is the source of truth
            _autoConnectLastProxy = config.AutoConnectLastProxy;
            _showNotifications = config.ShowNotifications;
            _dnsBypass = !config.DnsViaProxy;

            _proxyLabels.Clear();
            foreach (var kv in config.ProxyLabels ?? new())
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    _proxyLabels[kv.Key.Trim()] = kv.Value.Trim();
            _proxyChecks.Clear();
            foreach (var kv in config.ProxyChecks ?? new())
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    _proxyChecks[kv.Key.Trim()] = kv.Value;

            var migrate = config.RulesVersion < 2;
            var proxies = new List<string>();
            void AddRaw(string? raw)
            {
                raw = (raw ?? "").Trim();
                if (raw.Length > 0 && !proxies.Contains(raw) && ProxyParser.TryParse(raw, out _)) proxies.Add(raw);
            }
            foreach (var p in config.LoadedProxyList ?? new()) AddRaw(p);

            var mappings = (config.ProxyMappings ?? new())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ProcessName))
                .ToList();
            string defaultRaw;
            if (migrate)
            {
                // Before rules v2: recent proxies, mapping proxies and the current proxy all become saved proxies.
                foreach (var p in config.ProxyHistory ?? new()) AddRaw(p);
                foreach (var m in mappings) AddRaw(m.ProxyString);
                AddRaw(config.LastProxyInput);
                var last = (config.LastProxyInput ?? "").Trim();
                defaultRaw = mappings.Count == 0 && ProxyParser.TryParse(last, out _) ? last : "";
                Log($"Config migrated to rules v2: {mappings.Count} app rows, {proxies.Count} proxies, all other apps {(defaultRaw.Length > 0 ? "via proxy" : "direct")}");
            }
            else
            {
                defaultRaw = (config.DefaultProxy ?? "").Trim();
            }

            Proxies.Clear();
            while (ProxyChoices.Count > 1) ProxyChoices.RemoveAt(ProxyChoices.Count - 1);
            foreach (var raw in proxies)
            {
                ProxyParser.TryParse(raw, out var parsed);
                InsertProxy(CreateItem(raw, parsed));
            }

            Rules.Clear();
            foreach (var m in mappings)
            {
                m.ProcessName = m.ProcessName.Trim();
                m.ProxyString = (m.ProxyString ?? "").Trim();
                if (m.ProxyString.Length > 0 && FindProxy(m.ProxyString) == null) m.ProxyString = "";
                Rules.Add(CreateRow(m));
            }

            _defaultChoice = ChoiceFor(FindProxy(defaultRaw) != null ? defaultRaw : "");
            OnPropertyChanged(nameof(DefaultChoice));

            if (migrate)
            {
                _loading = false;
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load settings", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        if (_loading) return;
        try
        {
            // Load-modify-save keeps fields owned by other components (license cache, language, updates).
            var config = ConfigManager.LoadConfig();
            config.CloseToTray = _minimizeToTray;
            config.StartWithWindows = _startWithWindows;
            config.AutoConnectLastProxy = _autoConnectLastProxy;
            config.ShowNotifications = _showNotifications;
            config.DnsViaProxy = !_dnsBypass;
            config.DisableUdp = true;
            config.RulesVersion = 2;
            config.DefaultProxy = _defaultChoice.Raw;
            config.LastProxyInput = _defaultChoice.Raw;
            config.ConnectionMode = Rules.Any(r => r.HasApp) ? "apps" : "system";
            config.ProxyHistory = new List<string>();
            config.LoadedProxyList = Proxies.Select(p => p.Raw).ToList();
            config.ProxyMappings = Rules.Where(r => r.HasApp).Select(r => r.Mapping).ToList();
            config.ProxyLabels = new Dictionary<string, string>(_proxyLabels, StringComparer.Ordinal);
            config.ProxyChecks = Proxies
                .Where(p => _proxyChecks.ContainsKey(p.Raw))
                .ToDictionary(p => p.Raw, p => _proxyChecks[p.Raw], StringComparer.Ordinal);
            if (!ConfigManager.SaveConfig(config))
                AppLog.Error("Failed to save config.json");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save settings", ex);
        }
    }

    private void UpdateAutoStart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue("ProxyBridge", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("ProxyBridge", false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to update autostart", ex);
        }
    }

    private static bool IsAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("ProxyBridge") != null;
        }
        catch
        {
            return false;
        }
    }

    // ====================================================================
    // License
    // ====================================================================
    public bool HasLicense => _license.HasKey;

    public string LicensePlanText => !_license.HasKey
        ? I18n.T("license.none")
        : string.IsNullOrEmpty(_license.PlanDisplayName) ? I18n.T("license.generic") : _license.PlanDisplayName;

    public string LicenseExpiresText => !_license.HasKey
        ? ""
        : _license.ExpiresAt.HasValue
            ? I18n.T("license.valid_until", _license.ExpiresDisplay)
            : I18n.T("license.valid_forever");

    public string LicenseMaskedKey => _license.MaskedKey;

    /// <summary>Footer: "ЛИЦЕНЗИЯ  НАВСЕГДА  /  PB-TEST-••••" or "НЕТ ЛИЦЕНЗИИ".</summary>
    public string LicenseFooterText
    {
        get
        {
            if (!_license.HasKey) return I18n.T("footer.no_license");
            var plan = (string.IsNullOrEmpty(_license.PlanDisplayName) ? I18n.T("license.generic") : _license.PlanDisplayName)
                .ToUpper(I18n.Instance.Culture);
            var until = _license.ExpiresAt.HasValue ? "  /  " + I18n.T("license.valid_until", _license.ExpiresDisplay) : "";
            return I18n.T("footer.license", plan, _license.MaskedKey, until);
        }
    }

    public bool IsUnlinkConfirmVisible
    {
        get => _isUnlinkConfirmVisible;
        set { if (SetProperty(ref _isUnlinkConfirmVisible, value)) OnPropertyChanged(nameof(IsUnlinkButtonVisible)); }
    }

    public bool IsUnlinkButtonVisible => !_isUnlinkConfirmVisible;

    /// <summary>Re-reads the cached license (plan, expiry, masked key).</summary>
    public void RefreshLicenseInfo()
    {
        try
        {
            _license = LicenseService.Instance.GetCachedState();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to read license info", ex);
            _license = new LicenseState();
        }
        OnPropertyChanged(nameof(HasLicense));
        OnPropertyChanged(nameof(LicensePlanText));
        OnPropertyChanged(nameof(LicenseExpiresText));
        OnPropertyChanged(nameof(LicenseMaskedKey));
        OnPropertyChanged(nameof(LicenseFooterText));
    }

    private async Task UnlinkDevice()
    {
        if (_isUnlinking) return;
        _isUnlinking = true;
        try
        {
            try { await StopAllConnectionsAsync(); } catch { }
            await LicenseService.Instance.DeactivateAsync();
            LicenseService.Instance.ClearLicense();
            RefreshLicenseInfo();
            if (Avalonia.Application.Current is App app)
                app.ShowLicenseWindow("license.msg.unlinked");
        }
        catch (Exception ex)
        {
            AppLog.Error("Unlink device failed", ex);
        }
        finally
        {
            _isUnlinking = false;
            IsUnlinkConfirmVisible = false;
        }
    }

    // ====================================================================
    // Links
    // ====================================================================
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to open {url}", ex);
        }
    }
}
