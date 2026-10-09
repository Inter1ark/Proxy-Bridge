using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

/// <summary>Top tabs.</summary>
public enum AppPage { Home, Proxies, Settings }

public enum StatusKind { Neutral, Success, Warning, Error }

/// <summary>
/// A status message stored as an i18n key plus arguments, so it re-renders in the new
/// language when the user switches languages.
/// </summary>
public sealed class LocStatus : ViewModelBase
{
    private string _key = "";
    private object[] _args = Array.Empty<object>();
    private StatusKind _kind;

    public void Set(string key, StatusKind kind = StatusKind.Neutral, params object[] args)
    {
        _key = key ?? "";
        _args = args ?? Array.Empty<object>();
        _kind = kind;
        Refresh();
    }

    public void Clear() => Set("");

    public string Key => _key;
    public StatusKind Kind => _kind;
    public string Text => _key.Length == 0 ? "" : I18n.T(_key, _args);
    public bool HasText => _key.Length > 0;
    public bool IsNeutral => _kind == StatusKind.Neutral;
    public bool IsSuccess => _kind == StatusKind.Success;
    public bool IsWarning => _kind == StatusKind.Warning;
    public bool IsError => _kind == StatusKind.Error;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(IsNeutral));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
    }
}

/// <summary>A saved proxy (Proxies tab row, and the target of rules).</summary>
public sealed class ProxyItem : ViewModelBase
{
    private string _label = "";
    private bool _isEditing;
    private string _editLabel = "";
    private bool _isChecking;
    private ProxyCheckInfo? _check;

    public ProxyItem(string raw, ParsedProxy parsed)
    {
        Raw = raw;
        Parsed = parsed;
    }

    public string Raw { get; }
    public ParsedProxy Parsed { get; }
    public string Type => Parsed.Type;
    public string HostPort => Parsed.HostPort;
    /// <summary>Short masked login ("very-long-user-name…:••••"), never the full username.</summary>
    public string ShortAuth => Parsed.ShortAuth;
    public bool HasAuth => Parsed.HasAuth;
    /// <summary>Full masked login for the tooltip (password hidden).</summary>
    public string AuthTip => Parsed.MaskedAuth;

    /// <summary>Custom name ("" when not set).</summary>
    public string Label
    {
        get => _label;
        set
        {
            if (!SetProperty(ref _label, (value ?? "").Trim())) return;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(HasDistinctName));
            OnPropertyChanged(nameof(NoDistinctName));
        }
    }

    /// <summary>Display name: custom name, else the checked city, else the host.</summary>
    public string Name =>
        _label.Length > 0 ? _label
        : _check is { Ok: true } && _check.City.Length > 0 ? _check.City
        : Parsed.Host;

    /// <summary>True when the name is not just the host (a custom name or a checked city), so the address is shown next to it.</summary>
    public bool HasDistinctName => _label.Length > 0 || (_check is { Ok: true } && _check.City.Length > 0);
    public bool NoDistinctName => !HasDistinctName;

    public bool IsEditing
    {
        get => _isEditing;
        set { if (SetProperty(ref _isEditing, value)) OnPropertyChanged(nameof(IsNotEditing)); }
    }

    public bool IsNotEditing => !_isEditing;
    public string EditLabel { get => _editLabel; set => SetProperty(ref _editLabel, value ?? ""); }

    // ---------------- check state ----------------
    public ProxyCheckInfo? Check
    {
        get => _check;
        set
        {
            _check = value;
            RaiseCheck();
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetProperty(ref _isChecking, value)) RaiseCheck();
        }
    }

    public bool IsNotChecking => !_isChecking;
    public bool IsOk => !_isChecking && _check is { Ok: true };
    public bool IsFailed => !_isChecking && _check is { Ok: false };
    public bool IsUnchecked => !_isChecking && _check == null;
    public string CountryCode => _check is { Ok: true } ? _check.CountryCode : "";
    public int LatencyMs => _check is { Ok: true } ? _check.LatencyMs : 0;

    /// <summary>Status column, first line: "42 мс", "не проверен", "ошибка", "проверяю...".</summary>
    public string StatusMain =>
        _isChecking ? I18n.T("proxies.checking")
        : _check == null ? I18n.T("proxies.unchecked")
        : _check.Ok ? (_check.LatencyMs > 0 ? I18n.T("unit.ms", _check.LatencyMs) : I18n.T("proxies.works"))
        : I18n.T("proxies.error_prefix");

    /// <summary>Status column, second line: "Германия, Франкфурт" or the error reason.</summary>
    public string StatusSub =>
        _isChecking || _check == null ? ""
        : _check.Ok ? Place
        : I18n.T("check.err." + (_check.Error.Length > 0 ? _check.Error : ProxyChecker.ErrBadResponse));

    /// <summary>"Germany, Frankfurt" (country, city) after a successful check.</summary>
    public string Place => _check is { Ok: true }
        ? string.Join(", ", new[] { _check.Country, _check.City }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : "";

    /// <summary>Tooltip of the status column: exit IP and check time.</summary>
    public string StatusTip =>
        _check == null ? I18n.T("proxies.unchecked_tip")
        : _check.Ok ? I18n.T("proxies.ok_tip", _check.Ip, CheckedLocal)
        : I18n.T("proxies.failed_tip", CheckedLocal);

    private string CheckedLocal =>
        DateTime.TryParse(_check?.CheckedAt, I18n.Instance.Culture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("g", I18n.Instance.Culture)
            : "";

    public void RaiseCheck()
    {
        OnPropertyChanged(nameof(Check));
        OnPropertyChanged(nameof(IsNotChecking));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsUnchecked));
        OnPropertyChanged(nameof(CountryCode));
        OnPropertyChanged(nameof(LatencyMs));
        OnPropertyChanged(nameof(StatusMain));
        OnPropertyChanged(nameof(StatusSub));
        OnPropertyChanged(nameof(Place));
        OnPropertyChanged(nameof(StatusTip));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasDistinctName));
        OnPropertyChanged(nameof(NoDistinctName));
    }

    /// <summary>Re-renders the localized texts after a language change.</summary>
    public void RefreshTexts() => RaiseCheck();
}

/// <summary>Entry of a proxy picker: a saved proxy or "Direct".</summary>
public sealed class ProxyChoice
{
    public static ProxyChoice Direct { get; } = new(null);

    public ProxyChoice(ProxyItem? item) => Item = item;

    /// <summary>The proxy, or null for "Direct".</summary>
    public ProxyItem? Item { get; }
    public bool IsDirect => Item == null;
    public bool IsProxy => Item != null;
    /// <summary>Proxy string, "" for Direct.</summary>
    public string Raw => Item?.Raw ?? "";
    public override string ToString() => Item?.Name ?? "Direct";
}

/// <summary>One row of the rules table: a program and the proxy it uses.</summary>
public sealed class RuleRow : ViewModelBase
{
    private Bitmap? _icon;
    private ProxyChoice _choice = ProxyChoice.Direct;

    public RuleRow(AppProxyMapping mapping, ObservableCollection<ProxyChoice> choices)
    {
        Mapping = mapping;
        Choices = choices;
        LoadIcon();
    }

    public AppProxyMapping Mapping { get; }
    public ObservableCollection<ProxyChoice> Choices { get; }

    /// <summary>Raised after the user changed the program, proxy or switch.</summary>
    public event Action<RuleRow>? Changed;

    public string ProcessName => Mapping.ProcessName;
    public bool HasApp => Mapping.ProcessName.Trim().Length > 0;
    public bool HasNoApp => !HasApp;

    /// <summary>"Chrome" for chrome.exe.</summary>
    public string DisplayName => Pretty(Mapping.ProcessName);

    public static string Pretty(string process)
    {
        var n = Path.GetFileNameWithoutExtension((process ?? "").Trim());
        if (n.Length == 0) return "";
        return char.ToUpperInvariant(n[0]) + n.Substring(1);
    }

    public string Letter => HasApp ? char.ToUpperInvariant(Mapping.ProcessName.Trim()[0]).ToString() : "?";
    public string PathTip => Mapping.ExePath.Length > 0 ? Mapping.ExePath : Mapping.ProcessName;

    public Bitmap? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(ShowLetter));
            }
        }
    }

    public bool HasIcon => _icon != null;
    public bool ShowLetter => _icon == null && HasApp;

    public bool Enabled
    {
        get => Mapping.Enabled;
        set
        {
            if (Mapping.Enabled == value) return;
            Mapping.Enabled = value;
            OnPropertyChanged();
            Changed?.Invoke(this);
        }
    }

    /// <summary>Selected proxy. A null coming from the ComboBox (its list was rebuilt) is ignored.</summary>
    public ProxyChoice? Choice
    {
        get => _choice;
        set
        {
            if (value == null || ReferenceEquals(value, _choice)) return;
            _choice = value;
            Mapping.ProxyString = value.Raw;
            OnPropertyChanged();
            Changed?.Invoke(this);
        }
    }

    /// <summary>Sets the selection without raising <see cref="Changed"/> (load, list rebuild).</summary>
    public void SetChoiceSilently(ProxyChoice choice)
    {
        _choice = choice;
        Mapping.ProxyString = choice.Raw;
        OnPropertyChanged(nameof(Choice));
    }

    public void SetProgram(string exeName, string exePath)
    {
        Mapping.ProcessName = exeName;
        Mapping.ExePath = exePath ?? "";
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(HasApp));
        OnPropertyChanged(nameof(HasNoApp));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Letter));
        OnPropertyChanged(nameof(PathTip));
        Icon = null;
        LoadIcon();
        OnPropertyChanged(nameof(ShowLetter));
        Changed?.Invoke(this);
    }

    private void LoadIcon()
    {
        if (!HasApp) return;
        var name = Mapping.ProcessName;
        AppIconService.Request(name, Mapping.ExePath, bmp =>
        {
            if (string.Equals(Mapping.ProcessName, name, StringComparison.OrdinalIgnoreCase)) Icon = bmp;
        });
    }
}

/// <summary>A running program in the app picker.</summary>
public sealed class PickerApp : ViewModelBase
{
    private Bitmap? _icon;

    public PickerApp(RunningProgram p)
    {
        ExeName = p.ExeName;
        Path = p.Path;
        Name = RuleRow.Pretty(p.ExeName);
        Letter = Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
    }

    public string ExeName { get; }
    public string Path { get; }
    public string Name { get; }
    public string Letter { get; }

    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(ShowLetter));
        }
    }

    public bool ShowLetter => _icon == null;

    public bool Matches(string query) =>
        query.Length == 0
        || ExeName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(query, StringComparison.OrdinalIgnoreCase);
}
