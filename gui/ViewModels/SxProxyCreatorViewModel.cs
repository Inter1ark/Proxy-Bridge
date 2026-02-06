using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class SxProxyCreatorViewModel : ViewModelBase
{
    private readonly SxOrgApiService _api;
    private readonly SettingsService _settings;
    private Action _onClose;
    private readonly Action<string> _onProxyCreated;

    // === UI State ===
    private bool _isAuthorized;
    private bool _isLoading;
    private string _statusMessage = "";
    private string _apiKey = "";
    private string _balance = "0.00";

    // === Data Collections ===
    private ObservableCollection<SxCountry> _countries = new();
    private ObservableCollection<SxState> _states = new();
    private ObservableCollection<SxCity> _cities = new();
    private ObservableCollection<ProxyItem> _proxies = new();

    // === Selection ===
    private SxCountry? _selectedCountry;
    private SxState? _selectedState;
    private SxCity? _selectedCity;
    private int _selectedProtocol;      // 0=Residential, 1=Mobile, 2=Corporate
    private int _selectedMode;          // 0=Sticky, 1=Rotating
    private int _selectedProxyType;     // for XAML ComboBox binding
    private string _proxyName = "";
    private string _proxyCount = "1";
    private string _ttl = "60";

    // === Header display ===
    private string _mobileCount = "0";
    private string _residentialCount = "0";
    private string _datacenterCount = "0";
    private string _planName = "SX.ORG";

    public SxProxyCreatorViewModel(Action onClose, Action<string> onProxyCreated)
    {
        _api = new SxOrgApiService();
        _settings = new SettingsService();
        _onClose = onClose ?? (() => { });
        _onProxyCreated = onProxyCreated ?? ((_) => { });

        // Commands — AsyncRelayCommand catches ALL exceptions, never crashes
        AuthorizeCommand = new AsyncRelayCommand(AuthorizeAsync);
        CreateProxyCommand = new AsyncRelayCommand(CreateProxyAsync);
        LoadProxiesCommand = new AsyncRelayCommand(LoadProxiesAsync);
        UseProxyCommand = new RelayCommand<ProxyItem>(UseProxy);
        CloseCommand = new RelayCommand(DoClose);
        GetApiKeyCommand = new RelayCommand(OpenApiKeyUrl);

        // Auto-load saved API key and authorize
        TryAutoAuthorize();
    }

    private async void TryAutoAuthorize()
    {
        try
        {
            var settings = _settings.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.SxOrgApiKey))
            {
                await UIRun(() => { ApiKey = settings.SxOrgApiKey; });
                await AuthorizeAsync();
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Позволяет передать ссылку на закрытие окна после создания</summary>
    public void SetCloseAction(Action closeWindow)
    {
        _onClose = closeWindow ?? _onClose;
    }

    // === Properties ===
    public bool IsAuthorized { get => _isAuthorized; set => SetProperty(ref _isAuthorized, value); }
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string Balance { get => _balance; set => SetProperty(ref _balance, value); }
    public string PlanName { get => _planName; set => SetProperty(ref _planName, value); }
    public string MobileCount { get => _mobileCount; set => SetProperty(ref _mobileCount, value); }
    public string ResidentialCount { get => _residentialCount; set => SetProperty(ref _residentialCount, value); }
    public string DatacenterCount { get => _datacenterCount; set => SetProperty(ref _datacenterCount, value); }
    public string ProxyName { get => _proxyName; set => SetProperty(ref _proxyName, value); }
    public string ProxyCount { get => _proxyCount; set => SetProperty(ref _proxyCount, value); }
    public string Ttl { get => _ttl; set => SetProperty(ref _ttl, value); }
    public int SelectedProxyType { get => _selectedProxyType; set => SetProperty(ref _selectedProxyType, value); }

    public ObservableCollection<SxCountry> Countries { get => _countries; set => SetProperty(ref _countries, value); }
    public ObservableCollection<SxState> States { get => _states; set => SetProperty(ref _states, value); }
    public ObservableCollection<SxCity> Cities { get => _cities; set => SetProperty(ref _cities, value); }
    public ObservableCollection<ProxyItem> Proxies { get => _proxies; set => SetProperty(ref _proxies, value); }

    public SxCountry? SelectedCountry
    {
        get => _selectedCountry;
        set
        {
            if (SetProperty(ref _selectedCountry, value) && value != null)
                _ = SafeRun(() => LoadStatesAsync(value.Id));
        }
    }

    public SxState? SelectedState
    {
        get => _selectedState;
        set
        {
            if (SetProperty(ref _selectedState, value) && value != null && SelectedCountry != null)
                _ = SafeRun(() => LoadCitiesAsync(SelectedCountry.Id, value.Id));
        }
    }

    public SxCity? SelectedCity { get => _selectedCity; set => SetProperty(ref _selectedCity, value); }
    public int SelectedProtocol { get => _selectedProtocol; set => SetProperty(ref _selectedProtocol, value); }
    public int SelectedMode { get => _selectedMode; set => SetProperty(ref _selectedMode, value); }

    // === Commands ===
    public ICommand AuthorizeCommand { get; }
    public ICommand CreateProxyCommand { get; }
    public ICommand LoadProxiesCommand { get; }
    public ICommand UseProxyCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand GetApiKeyCommand { get; }

    // ========================================================================
    // SAFE ASYNC WRAPPER — prevents ALL unhandled exceptions from crashing app
    // ========================================================================
    private async Task SafeRun(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SX.ORG] SafeRun ERROR: {ex}");
            try { await UIRun(() => StatusMessage = $"❌ {ex.Message}"); } catch { }
        }
    }

    // Helper: always update UI on the correct thread
    private async Task UIRun(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    // ========================================================================
    // AUTHORIZE
    // ========================================================================
    private async Task AuthorizeAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            await UIRun(() => StatusMessage = "❌ Введите API ключ");
            return;
        }

        await UIRun(() => { IsLoading = true; StatusMessage = "🔄 Проверка ключа..."; });

        try
        {
            _api.SetApiKey(ApiKey.Trim());
            var (isValid, balance) = await _api.ValidateKeyAsync();

            if (isValid)
            {
                // Save API key for next time
                try
                {
                    var settings = _settings.LoadSettings();
                    settings.SxOrgApiKey = ApiKey.Trim();
                    _settings.SaveSettings(settings);
                }
                catch { }

                await UIRun(() =>
                {
                    IsAuthorized = true;
                    Balance = balance;
                    StatusMessage = "✅ Авторизация успешна!";
                });

                // Load data safely — failures here don't crash the app
                await SafeRun(LoadCountriesAsync);
                await SafeRun(LoadProxiesAsync);
            }
            else
            {
                await UIRun(() => StatusMessage = "❌ Неверный API ключ");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SX.ORG] AuthorizeAsync ERROR: {ex}");
            await UIRun(() => StatusMessage = $"❌ Ошибка: {ex.Message}");
        }
        finally
        {
            await UIRun(() => IsLoading = false);
        }
    }

    // ========================================================================
    // LOAD COUNTRIES
    // ========================================================================
    private async Task LoadCountriesAsync()
    {
        var countries = await _api.GetCountriesAsync();
        if (countries.Count > 0)
        {
            await UIRun(() =>
            {
                Countries.Clear();
                foreach (var c in countries)
                    Countries.Add(c);
            });
        }
    }

    // ========================================================================
    // LOAD STATES
    // ========================================================================
    private async Task LoadStatesAsync(int countryId)
    {
        await UIRun(() =>
        {
            States.Clear();
            Cities.Clear();
            _selectedState = null;
            OnPropertyChanged(nameof(SelectedState));
            _selectedCity = null;
            OnPropertyChanged(nameof(SelectedCity));
        });

        var states = await _api.GetStatesAsync(countryId);
        if (states.Count > 0)
        {
            await UIRun(() =>
            {
                foreach (var s in states)
                    States.Add(s);
            });
        }
    }

    // ========================================================================
    // LOAD CITIES
    // ========================================================================
    private async Task LoadCitiesAsync(int countryId, int stateId)
    {
        await UIRun(() =>
        {
            Cities.Clear();
            _selectedCity = null;
            OnPropertyChanged(nameof(SelectedCity));
        });

        var cities = await _api.GetCitiesAsync(countryId, stateId);
        if (cities.Count > 0)
        {
            await UIRun(() =>
            {
                foreach (var c in cities)
                    Cities.Add(c);
            });
        }
    }

    // ========================================================================
    // LOAD EXISTING PROXIES
    // ========================================================================
    private async Task LoadProxiesAsync()
    {
        await UIRun(() => StatusMessage = "🔄 Загрузка прокси...");

        var proxies = await _api.GetProxyPortsAsync();
        await UIRun(() =>
        {
            Proxies.Clear();
            if (proxies.Count > 0)
            {
                foreach (var p in proxies)
                {
                    Proxies.Add(new ProxyItem
                    {
                        Name = string.IsNullOrEmpty(p.Name) ? $"{p.Server}:{p.Port}" : p.Name,
                        ProxyString = $"http://{p.Login}:{p.Password}@{p.Server}:{p.Port}",
                        Country = p.Country ?? p.CountryCode ?? "",
                        Status = p.Status ?? "active"
                    });
                }
                StatusMessage = $"✅ Загружено {proxies.Count} прокси";
            }
            else
            {
                StatusMessage = "ℹ️ Прокси пока нет";
            }
        });
    }

    // ========================================================================
    // CREATE PROXY
    // ========================================================================
    private async Task CreateProxyAsync()
    {
        if (SelectedCountry == null)
        {
            await UIRun(() => StatusMessage = "❌ Выберите страну");
            return;
        }

        await UIRun(() => { IsLoading = true; StatusMessage = "🔄 Создание прокси..."; });

        try
        {
            var typeId = SelectedMode == 0 ? 2 : 3;          // 2=sticky, 3=rotate
            var proxyTypeId = SelectedProtocol switch
            {
                0 => 1,  // Residential
                1 => 3,  // Mobile
                2 => 4,  // Corporate
                _ => 1
            };

            var name = string.IsNullOrWhiteSpace(ProxyName)
                ? $"ProxyBridge_{DateTime.Now:HHmmss}"
                : ProxyName.Trim();

            var countryName = SelectedCountry.Name;

            var result = await _api.CreateProxyAsync(
                countryCode: SelectedCountry.Code,
                typeId: typeId,
                proxyTypeId: proxyTypeId,
                proxyName: name,
                state: SelectedState?.Name,
                city: SelectedCity?.Name
            );

            if (result.Count > 0)
            {
                await UIRun(() =>
                {
                    foreach (var p in result)
                    {
                        Proxies.Insert(0, new ProxyItem
                        {
                            Name = string.IsNullOrEmpty(p.Name) ? name : p.Name,
                            ProxyString = $"http://{p.Login}:{p.Password}@{p.Server}:{p.Port}",
                            Country = countryName,
                            Status = "active"
                        });
                    }
                    StatusMessage = $"✅ Создано {result.Count} прокси!";
                });

                // Refresh balance silently
                await SafeRun(async () =>
                {
                    var (_, balance) = await _api.ValidateKeyAsync();
                    await UIRun(() => Balance = balance);
                });
            }
            else
            {
                await UIRun(() => StatusMessage = "❌ Не удалось создать прокси");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SX.ORG] CreateProxy ERROR: {ex}");
            await UIRun(() => StatusMessage = $"❌ Ошибка: {ex.Message}");
        }
        finally
        {
            await UIRun(() => IsLoading = false);
        }
    }

    // ========================================================================
    // USE / CLOSE
    // ========================================================================
    private void UseProxy(ProxyItem? proxy)
    {
        try
        {
            if (proxy != null && !string.IsNullOrEmpty(proxy.ProxyString))
            {
                _onProxyCreated(proxy.ProxyString);
                _onClose();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[SX.ORG] UseProxy ERROR: {ex}"); }
    }

    private void DoClose()
    {
        try { _onClose(); } catch { }
    }

    private void OpenApiKeyUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://sx.org/?c=proxybridge", UseShellExecute = true });
        }
        catch { }
    }
}

// ============================================================================
// PROXY ITEM for display in UI
// ============================================================================
public class ProxyItem
{
    public string Name { get; set; } = "";
    public string ProxyString { get; set; } = "";
    public string Country { get; set; } = "";
    public string Status { get; set; } = "active";
}

// ============================================================================
// ASYNC RELAY COMMAND — catches ALL exceptions, NEVER crashes the app
// ============================================================================
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        if (_isExecuting) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // NEVER let exceptions escape — this is the last line of defense
            Debug.WriteLine($"[AsyncRelayCommand] CAUGHT: {ex}");
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    private void RaiseCanExecuteChanged()
    {
        try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }
}
