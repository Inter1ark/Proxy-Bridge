using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class LicenseViewModel : ViewModelBase
{
    private readonly LicenseService _license;
    private readonly bool _autoVerify;

    private string _keyText = "";
    private bool _isBusy;

    /// <summary>Raised on the UI thread after a successful activation / verification.</summary>
    public event Action? Activated;

    public LicenseViewModel() : this(LicenseService.Instance, false, null) { }

    /// <param name="initialMessageKey">Optional i18n key shown as an error on open (for example "license.msg.unlinked").</param>
    public LicenseViewModel(LicenseService license, bool autoVerify, string? initialMessageKey)
    {
        _license = license;
        _autoVerify = autoVerify;

        var cached = _license.GetCachedState();
        if (cached.HasKey) _keyText = cached.Key;

        if (!string.IsNullOrWhiteSpace(initialMessageKey))
            Status.Set(initialMessageKey, initialMessageKey == "license.msg.unlinked" ? StatusKind.Neutral : StatusKind.Error);

        ActivateCommand = new RelayCommand(async () => await ActivateAsync(), () => !_isBusy);
        BuyCommand = new RelayCommand(OpenBuyPage);
        SetRussianCommand = new RelayCommand(() => I18n.Instance.SetLanguageAndSave(I18n.Russian));
        SetEnglishCommand = new RelayCommand(() => I18n.Instance.SetLanguageAndSave(I18n.English));

        I18n.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Status line under the key field (re-localized on language change).</summary>
    public LocStatus Status { get; } = new();

    public string KeyText
    {
        get => _keyText;
        set => SetProperty(ref _keyText, (value ?? "").ToUpperInvariant());
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                (ActivateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !_isBusy;

    public bool IsRussian => I18n.Instance.IsRussian;
    public bool IsEnglish => !I18n.Instance.IsRussian;

    public ICommand ActivateCommand { get; }
    public ICommand BuyCommand { get; }
    public ICommand SetRussianCommand { get; }
    public ICommand SetEnglishCommand { get; }

    private void OnLanguageChanged()
    {
        Status.Refresh();
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(IsEnglish));
    }

    public void Detach()
    {
        I18n.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Called by the window once it is opened: runs the online verification for a stale cache.</summary>
    public async Task OnOpenedAsync()
    {
        if (!_autoVerify) return;

        IsBusy = true;
        Status.Set("license.msg.verifying");
        try
        {
            var result = await _license.VerifyForStartupAsync();
            if (result.Ok)
            {
                Status.Set("license.msg.verified", StatusKind.Success);
                Activated?.Invoke();
                return;
            }

            Status.Set(LicenseService.ErrorToKey(result.Error), StatusKind.Error, result.Error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] verify failed: {ex.Message}");
            Status.Set(LicenseService.ErrorToKey("network"), StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ActivateAsync()
    {
        if (IsBusy) return;

        var key = LicenseService.NormalizeKey(KeyText);
        if (!LicenseService.IsValidKeyFormat(key))
        {
            Status.Set("license.msg.bad_format", StatusKind.Error);
            return;
        }
        KeyText = key;

        IsBusy = true;
        Status.Set("license.msg.activating");
        try
        {
            var result = await _license.ActivateAsync(key);
            if (result.Ok)
            {
                var plan = LicenseService.PlanToDisplayName(result.Plan);
                if (string.IsNullOrEmpty(plan))
                    Status.Set("license.msg.activated", StatusKind.Success);
                else
                    Status.Set("license.msg.activated_plan", StatusKind.Success, plan);
                Activated?.Invoke();
                return;
            }

            Status.Set(LicenseService.ErrorToKey(result.Error), StatusKind.Error, result.Error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] activate failed: {ex.Message}");
            Status.Set(LicenseService.ErrorToKey("network"), StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void OpenBuyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LicenseService.BuyUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // browser could not be opened
        }
    }
}
