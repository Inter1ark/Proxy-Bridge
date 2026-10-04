using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class LicenseViewModel : ViewModelBase
{
    public const string ErrorColor = "#f87171";
    public const string SuccessColor = "#4ade80";
    public const string NeutralColor = "#8b9dc3";

    private readonly LicenseService _license;
    private readonly bool _autoVerify;

    private string _keyText = "";
    private string _statusText = "";
    private string _statusColor = NeutralColor;
    private bool _isBusy;

    /// <summary>Raised on the UI thread after a successful activation / verification.</summary>
    public event Action? Activated;

    public LicenseViewModel() : this(LicenseService.Instance, false, null) { }

    public LicenseViewModel(LicenseService license, bool autoVerify, string? initialMessage)
    {
        _license = license;
        _autoVerify = autoVerify;

        var cached = _license.GetCachedState();
        if (cached.HasKey) _keyText = cached.Key;

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            _statusText = initialMessage;
            _statusColor = ErrorColor;
        }

        ActivateCommand = new RelayCommand(async () => await ActivateAsync(), () => !_isBusy);
        BuyCommand = new RelayCommand(OpenBuyPage);
    }

    public string KeyText
    {
        get => _keyText;
        set
        {
            var upper = (value ?? "").ToUpperInvariant();
            SetProperty(ref _keyText, upper);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
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

    public ICommand ActivateCommand { get; }
    public ICommand BuyCommand { get; }

    /// <summary>Called by the window once it is opened: runs the online verification for a stale cache.</summary>
    public async Task OnOpenedAsync()
    {
        if (!_autoVerify) return;

        IsBusy = true;
        StatusText = "Проверка лицензии...";
        StatusColor = NeutralColor;
        try
        {
            var result = await _license.VerifyForStartupAsync();
            if (result.Ok)
            {
                StatusText = "Лицензия подтверждена";
                StatusColor = SuccessColor;
                Activated?.Invoke();
                return;
            }

            StatusText = result.Message;
            StatusColor = ErrorColor;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] verify failed: {ex.Message}");
            StatusText = LicenseService.ErrorToMessage("network");
            StatusColor = ErrorColor;
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
            StatusText = "Введите ключ в формате PB-XXXX-XXXX-XXXX-XXXX";
            StatusColor = ErrorColor;
            return;
        }
        KeyText = key;

        IsBusy = true;
        StatusText = "Активация...";
        StatusColor = NeutralColor;
        try
        {
            var result = await _license.ActivateAsync(key);
            if (result.Ok)
            {
                var plan = LicenseService.PlanToDisplayName(result.Plan);
                StatusText = string.IsNullOrEmpty(plan) ? "Лицензия активирована" : $"Лицензия активирована: {plan}";
                StatusColor = SuccessColor;
                Activated?.Invoke();
                return;
            }

            StatusText = result.Message;
            StatusColor = ErrorColor;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[License] activate failed: {ex.Message}");
            StatusText = LicenseService.ErrorToMessage("network");
            StatusColor = ErrorColor;
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
