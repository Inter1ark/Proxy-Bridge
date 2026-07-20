using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Win32;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private static void Log(string msg)
    {
        Debug.WriteLine($"[ProxyBridge] [{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
    
    private ProxyBridgeService? _proxyService;
    private Window? _mainWindow;
    private DispatcherTimer? _statsTimer;
    private DispatcherTimer? _connectionTimer;
    private DateTime _connectionStartTime;
    private Random _random = new Random();

    // Основные свойства
    private string _proxyInputText = "";
    private string _statusText = "ProxyBridge Stopped. Add proxy to start.";
    private string _connectButtonText = "CONNECT";
    private bool _isProxyActive = false;
    private uint _globalRuleId = 0;

    // Текущие настройки прокси
    private string _currentProxyType = "SOCKS5";
    private string _currentProxyIp = "";
    private string _currentProxyPort = "";
    private string _currentProxyUsername = "";
    private string _currentProxyPassword = "";

    // Статистика
    private string _uploadSpeed = "0 MB/s";
    private string _downloadSpeed = "0 MB/s";
    private string _connectionTime = "0 ms";
    private string _ping = "-- ms";

    // История прокси
    private ObservableCollection<string> _proxyHistory = new();

    // Proxy verification result
    private string _proxyGeoInfo = "";
    private bool _isProxyGeoVisible = false;

    // Навигация
    private bool _isDashboardVisible = true;
    private bool _isProxyListVisible = false;
    private bool _isBranchingVisible = false;
    private bool _isSettingsVisible = false;
    private bool _isHelpVisible = false;

    // Фоновые цвета для навигации
    private string _dashboardBg = "#252836";
    private string _proxyListBg = "Transparent";
    private string _branchingBg = "Transparent";
    private string _settingsBg = "Transparent";
    private string _helpBg = "Transparent";

    // Proxy List
    private ObservableCollection<string> _loadedProxyList = new();

    // Разветвление (per-app proxy)
    private ObservableCollection<AppProxyMapping> _branchingMappings = new();
    private ObservableCollection<string> _availableProxies = new();
    private string _newMappingProcess = "";
    private string _newMappingProxy = "";
    private string _branchingStatus = "Assign proxies to programs, then press Apply.";
    private bool _isBranchingActive = false;
    private readonly System.Collections.Generic.List<uint> _branchingRuleIds = new();

    // Settings
    private bool _minimizeToTray = true;
    private bool _startWithWindows = false;
    private bool _autoConnectLastProxy = false;
    private bool _showNotifications = true;
    private bool _dnsBypass = true; // DNS Direct (simple and fast)
    private bool _disableUdp = true; // TCP-only mode (like antidetect browser) // true = только TCP (исправляет ошибки UDP ASSOCIATE)

    // Properties
    public string ProxyInputText
    {
        get => _proxyInputText;
        set
        {
            if (SetProperty(ref _proxyInputText, value))
            {
                // Автоматический парсинг при вводе текста
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (ParseProxyInput(value))
                    {
                        StatusText = $"✅ Proxy parsed: {_currentProxyIp}:{_currentProxyPort}";
                    }
                    else
                    {
                        StatusText = "⚠️ Invalid proxy format. Use: ip:port:user:pass or socks5://user:pass@ip:port";
                    }
                }
                else
                {
                    StatusText = "ProxyBridge Stopped. Add proxy to start.";
                }
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ConnectButtonText
    {
        get => _connectButtonText;
        set => SetProperty(ref _connectButtonText, value);
    }

    public string UploadSpeed
    {
        get => _uploadSpeed;
        set => SetProperty(ref _uploadSpeed, value);
    }

    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set => SetProperty(ref _downloadSpeed, value);
    }

    public string ConnectionTime
    {
        get => _connectionTime;
        set => SetProperty(ref _connectionTime, value);
    }

    public string Ping
    {
        get => _ping;
        set => SetProperty(ref _ping, value);
    }

    public ObservableCollection<string> ProxyHistory
    {
        get => _proxyHistory;
        set => SetProperty(ref _proxyHistory, value);
    }

    public string ProxyGeoInfo
    {
        get => _proxyGeoInfo;
        set => SetProperty(ref _proxyGeoInfo, value);
    }

    public bool IsProxyGeoVisible
    {
        get => _isProxyGeoVisible;
        set => SetProperty(ref _isProxyGeoVisible, value);
    }

    // Navigation Properties
    public bool IsDashboardVisible
    {
        get => _isDashboardVisible;
        set => SetProperty(ref _isDashboardVisible, value);
    }

    public bool IsProxyListVisible
    {
        get => _isProxyListVisible;
        set => SetProperty(ref _isProxyListVisible, value);
    }

    public bool IsBranchingVisible
    {
        get => _isBranchingVisible;
        set => SetProperty(ref _isBranchingVisible, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => SetProperty(ref _isSettingsVisible, value);
    }

    public bool IsHelpVisible
    {
        get => _isHelpVisible;
        set => SetProperty(ref _isHelpVisible, value);
    }

    public string DashboardBg
    {
        get => _dashboardBg;
        set => SetProperty(ref _dashboardBg, value);
    }

    public string ProxyListBg
    {
        get => _proxyListBg;
        set => SetProperty(ref _proxyListBg, value);
    }

    public string BranchingBg
    {
        get => _branchingBg;
        set => SetProperty(ref _branchingBg, value);
    }

    public string SettingsBg
    {
        get => _settingsBg;
        set => SetProperty(ref _settingsBg, value);
    }

    public string HelpBg
    {
        get => _helpBg;
        set => SetProperty(ref _helpBg, value);
    }

    // Proxy List Properties
    public ObservableCollection<string> LoadedProxyList
    {
        get => _loadedProxyList;
        set => SetProperty(ref _loadedProxyList, value);
    }

    // === Разветвление Properties ===
    public ObservableCollection<AppProxyMapping> BranchingMappings
    {
        get => _branchingMappings;
        set => SetProperty(ref _branchingMappings, value);
    }

    public ObservableCollection<string> AvailableProxies
    {
        get => _availableProxies;
        set => SetProperty(ref _availableProxies, value);
    }

    public string NewMappingProcess
    {
        get => _newMappingProcess;
        set => SetProperty(ref _newMappingProcess, value);
    }

    public string NewMappingProxy
    {
        get => _newMappingProxy;
        set => SetProperty(ref _newMappingProxy, value);
    }

    public string BranchingStatus
    {
        get => _branchingStatus;
        set => SetProperty(ref _branchingStatus, value);
    }

    public bool IsBranchingActive
    {
        get => _isBranchingActive;
        set { if (SetProperty(ref _isBranchingActive, value)) OnPropertyChanged(nameof(BranchingButtonText)); }
    }

    public string BranchingButtonText => _isBranchingActive ? "STOP SPLIT TUNNELING" : "APPLY SPLIT TUNNELING";

    // Settings Properties
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
        set { if (SetProperty(ref _dnsBypass, value)) SaveSettings(); }
    }

    public bool DisableUdp
    {
        get => _disableUdp;
        set { if (SetProperty(ref _disableUdp, value)) SaveSettings(); }
    }

    // Commands
    public ICommand ToggleConnectionCommand { get; }
    public ICommand TestProxyCommand { get; }
    public ICommand ShowDashboardCommand { get; }
    public ICommand ShowProxyListCommand { get; }
    public ICommand ShowBranchingCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowHelpCommand { get; }
    public ICommand BrowseExeForMappingCommand { get; }
    public ICommand AddMappingCommand { get; }
    public ICommand RemoveMappingCommand { get; }
    public ICommand ToggleBranchingCommand { get; }
    public ICommand LoadProxyListCommand { get; }
    public ICommand ClearProxyListCommand { get; }
    public ICommand ClearProxyHistoryCommand { get; }
    public ICommand OpenTelegramCommand { get; }
    public ICommand SelectProxyFromHistoryCommand { get; }

    public MainWindowViewModel()
    {
        ToggleConnectionCommand = new RelayCommand(async () => await ToggleConnection());
        TestProxyCommand = new RelayCommand(async () => await TestProxy());
        ShowDashboardCommand = new RelayCommand(() => ShowTab("Dashboard"));
        ShowProxyListCommand = new RelayCommand(() => ShowTab("ProxyList"));
        ShowBranchingCommand = new RelayCommand(() => ShowTab("Branching"));
        ShowSettingsCommand = new RelayCommand(() => ShowTab("Settings"));
        ShowHelpCommand = new RelayCommand(() => ShowTab("Help"));
        BrowseExeForMappingCommand = new RelayCommand(async () => await BrowseExeForMapping());
        AddMappingCommand = new RelayCommand(() => AddMapping());
        RemoveMappingCommand = new RelayCommand<AppProxyMapping>((m) => RemoveMapping(m));
        ToggleBranchingCommand = new RelayCommand(async () => await ToggleBranching());
        LoadProxyListCommand = new RelayCommand(async () => await LoadProxyList());
        ClearProxyListCommand = new RelayCommand(() => { LoadedProxyList.Clear(); SaveSettings(); });
        ClearProxyHistoryCommand = new RelayCommand(() => { ProxyHistory.Clear(); SaveSettings(); });
        OpenTelegramCommand = new RelayCommand(() => OpenTelegram());
        SelectProxyFromHistoryCommand = new RelayCommand<string>((proxy) => SelectProxyFromHistory(proxy));

        // Инициализация таймеров
        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statsTimer.Tick += UpdateStats;

        _connectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _connectionTimer.Tick += UpdateConnectionTime;

        // Загрузка сохраненных настроек
        LoadSettings();

        // История прокси загружается из настроек
    }

    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
    }

    public void Initialize(ProxyBridgeService proxyService)
    {
        _proxyService = proxyService;
        // Подписываемся на нативные логи — чтобы видеть ошибки WinDivert и т.д.
        _proxyService.LogReceived += (msg) => Log($"[NATIVE] {msg}");
    }

    public void Cleanup()
    {
        SaveSettings();
        
        _statsTimer?.Stop();
        _connectionTimer?.Stop();
        
        if (_isProxyActive && _globalRuleId > 0)
        {
            try
            {
                _proxyService?.DeleteRule(_globalRuleId);
            }
            catch { }
        }
        
        try { _proxyService?.Stop(); } catch { }
    }

    private void ShowTab(string tabName)
    {
        IsDashboardVisible = tabName == "Dashboard";
        IsProxyListVisible = tabName == "ProxyList";
        IsBranchingVisible = tabName == "Branching";
        IsSettingsVisible = tabName == "Settings";
        IsHelpVisible = tabName == "Help";

        // Обновляем фоны
        DashboardBg = tabName == "Dashboard" ? "#252836" : "Transparent";
        ProxyListBg = tabName == "ProxyList" ? "#252836" : "Transparent";
        BranchingBg = tabName == "Branching" ? "#252836" : "Transparent";
        SettingsBg = tabName == "Settings" ? "#252836" : "Transparent";
        HelpBg = tabName == "Help" ? "#252836" : "Transparent";

        if (tabName == "Branching")
            RefreshAvailableProxies();
    }

    private async Task ToggleConnection()
    {
        if (_isProxyActive)
        {
            await DisconnectProxy();
        }
        else
        {
            await ConnectProxy();
        }
    }

    private async Task ConnectProxy()
    {
        if (_proxyService == null)
        {
            StatusText = "Service not initialized";
            return;
        }

        // Split tunneling and regular proxy are mutually exclusive
        if (_isBranchingActive)
        {
            StatusText = "❌ Split Tunneling is active. Stop it before a regular connection.";
            return;
        }

        // Если прокси еще не введен
        if (string.IsNullOrWhiteSpace(ProxyInputText))
        {
            StatusText = "Please enter proxy details";
            return;
        }

        // Если данные не распарсены, парсим
        if (string.IsNullOrWhiteSpace(_currentProxyIp) || string.IsNullOrWhiteSpace(_currentProxyPort))
        {
            if (!ParseProxyInput(ProxyInputText))
            {
                StatusText = "Invalid proxy format";
                return;
            }
        }

        try
        {
            Log("\n=== CONNECTING TO PROXY ===");
            Log($"Proxy Type: {_currentProxyType}");
            Log($"Proxy IP: {_currentProxyIp}");
            Log($"Proxy Port: {_currentProxyPort}");
            Log($"Username: {_currentProxyUsername}");
            Log($"Password: {_currentProxyPassword}");
            Log($"DNS Bypass: {DnsBypass}");
            Log($"Disable UDP: {DisableUdp}");
            
            // ВАЖНО: Сначала конфигурируем прокси ПЕРЕД Start(),
            // потому что нативный ProxyBridge_Start() использует g_proxy_ip и g_proxy_type
            // для построения WinDivert фильтра и решения, нужен ли UDP relay
            Log($"[STEP 1] Configuring proxy BEFORE start: type={_currentProxyType}, ip={_currentProxyIp}, port={_currentProxyPort}");
            Log($"[STEP 1] Username length: {_currentProxyUsername.Length}, Password length: {_currentProxyPassword.Length}");
            if (!_proxyService.SetProxyConfig(
                _currentProxyType,
                _currentProxyIp,
                ushort.Parse(_currentProxyPort),
                _currentProxyUsername,
                _currentProxyPassword))
            {
                Log(">>> FAILED at STEP 1: SetProxyConfig() returned false");
                StatusText = $"❌ Failed to configure proxy ({_currentProxyType} {_currentProxyIp}:{_currentProxyPort})";
                return;
            }
            Log("[STEP 1] ✓ Proxy configured OK");

            // Настраиваем Disable UDP (ПЕРЕД Start)
            if (DisableUdp)
            {
                Log("[STEP 1.5] Disabling UDP relay...");
                _proxyService.SetDisableUdp(true);
            }
            else
            {
                _proxyService.SetDisableUdp(false);
                Log("[STEP 1.5] UDP relay enabled");
            }

            // Теперь запускаем сервис (WinDivert + relay)
            Log("[STEP 2] Starting ProxyBridge service...");
            if (!_proxyService.Start())
            {
                Log(">>> FAILED at STEP 2: Start() returned false — see [NATIVE] logs above for WinDivert error");
                StatusText = "❌ Failed to start service (WinDivert). Run as Administrator!";
                return;
            }
            Log("[STEP 2] ✓ Service started OK");

            // Простая конфигурация - локальные IP Direct, остальное через прокси
            Log("[STEP 3] Adding Direct rules for local IPs...");
            var r1 = _proxyService.AddRule("*", "127.*.*.*", "*", "BOTH", "DIRECT");
            var r2 = _proxyService.AddRule("*", "10.*.*.*", "*", "BOTH", "DIRECT");
            var r3 = _proxyService.AddRule("*", "172.16.*.*-172.31.*.*", "*", "BOTH", "DIRECT");
            var r4 = _proxyService.AddRule("*", "192.168.*.*", "*", "BOTH", "DIRECT");
            var r5 = _proxyService.AddRule("*", "224.*.*.*", "*", "BOTH", "DIRECT");
            var r6 = _proxyService.AddRule("*", "255.255.255.255", "*", "BOTH", "DIRECT");
            Log($"[STEP 3] Local rules IDs: {r1}, {r2}, {r3}, {r4}, {r5}, {r6}");

            // Создаем правило для всего трафика
            Log("[STEP 4] Creating global proxy rule (* -> PROXY)...");
            _globalRuleId = _proxyService.AddRule("*", "*", "*", "BOTH", "PROXY");
            Log($"[STEP 4] Global rule ID: {_globalRuleId}");

            if (_globalRuleId > 0)
            {
                Log("=== PROXY CONNECTED SUCCESSFULLY ===\n");
                
                _isProxyActive = true;
                ConnectButtonText = "DISCONNECT";
                StatusText = "✅ Connected successfully!";
                
                // Добавляем в историю ИСХОДНЫЙ текст прокси (как ввел пользователь)
                string proxyToSave = ProxyInputText.Trim();
                if (!string.IsNullOrWhiteSpace(proxyToSave) && !ProxyHistory.Contains(proxyToSave))
                {
                    ProxyHistory.Insert(0, proxyToSave);
                    if (ProxyHistory.Count > 10)
                    {
                        ProxyHistory.RemoveAt(ProxyHistory.Count - 1);
                    }
                    SaveSettings(); // Save history to file
                }

                // Запускаем таймеры
                _connectionStartTime = DateTime.Now;
                _statsTimer?.Start();
                _connectionTimer?.Start();

                if (ShowNotifications)
                {
                    // Можно добавить системное уведомление
                }
            }
            else
            {
                Log(">>> FAILED at STEP 4: AddRule returned 0 (global rule not created)");
                StatusText = "❌ Failed to create routing rule (global PROXY rule)";
                _proxyService.Stop();
            }
        }
        catch (Exception ex)
        {
            Log($"!!! EXCEPTION in ConnectProxy: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
            StatusText = $"❌ Error: {ex.Message}";
            try
            {
                _proxyService.Stop();
            }
            catch { }
        }
    }

    private async Task DisconnectProxy()
    {
        if (_proxyService == null)
            return;

        try
        {
            if (_globalRuleId > 0)
            {
                _proxyService.DeleteRule(_globalRuleId);
                _globalRuleId = 0;
            }

            _proxyService.Stop();

            _isProxyActive = false;
            ConnectButtonText = "CONNECT";
            StatusText = "ProxyBridge Stopped. Add proxy to start.";

            // Останавливаем таймеры
            _statsTimer?.Stop();
            _connectionTimer?.Stop();

            // Сбрасываем статистику
            UploadSpeed = "0 MB/s";
            DownloadSpeed = "0 MB/s";
            ConnectionTime = "0 ms";
            Ping = "-- ms";

            if (ShowNotifications)
            {
                // Можно добавить системное уведомление
            }
        }
        catch (Exception ex)
        {
            StatusText = $"❌ Error during disconnect: {ex.Message}";
        }
    }

    private async Task TestProxy()
    {
        if (string.IsNullOrWhiteSpace(ProxyInputText))
        {
            StatusText = "Please enter proxy details";
            IsProxyGeoVisible = false;
            return;
        }

        // Parse proxy first
        if (!ParseProxyInput(ProxyInputText))
        {
            StatusText = "⚠️ Invalid proxy format";
            IsProxyGeoVisible = false;
            return;
        }

        StatusText = "🔄 Testing proxy...";
        IsProxyGeoVisible = false;

        try
        {
            if (_currentProxyType.ToUpper() == "HTTP")
            {
                // HTTP proxy with proper authentication
                var proxy = new WebProxy($"http://{_currentProxyIp}:{_currentProxyPort}")
                {
                    Credentials = new NetworkCredential(_currentProxyUsername, _currentProxyPassword)
                };

                var handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true
                };

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                
                // Get IP through proxy
                var ipResponse = await client.GetStringAsync("https://api.ipify.org?format=text");
                var proxyIp = ipResponse.Trim();

                // Get GEO info
                var geoResponse = await client.GetStringAsync($"http://ip-api.com/json/{proxyIp}");
                var geoData = JsonSerializer.Deserialize<JsonElement>(geoResponse);

                if (geoData.TryGetProperty("status", out var status) && status.GetString() == "success")
                {
                    var country = geoData.GetProperty("country").GetString() ?? "Unknown";
                    var city = geoData.GetProperty("city").GetString() ?? "Unknown";
                    var countryCode = geoData.GetProperty("countryCode").GetString() ?? "";

                    // Get flag emoji
                    string flag = GetCountryFlag(countryCode);

                    ProxyGeoInfo = $"{flag} {country}, {city} • IP: {proxyIp} • ✅ Valid";
                    IsProxyGeoVisible = true;
                    StatusText = "✅ Proxy is valid and working!";
                }
                else
                {
                    ProxyGeoInfo = "❌ Could not retrieve GEO data";
                    IsProxyGeoVisible = true;
                    StatusText = "⚠️ Proxy works but GEO lookup failed";
                }
            }
            else
            {
                // SOCKS5 - cannot test directly with HttpClient
                ProxyGeoInfo = "⚠️ SOCKS5 проверка не поддерживается. Используйте CONNECT для теста.";
                IsProxyGeoVisible = true;
                StatusText = "⚠️ SOCKS5 прокси можно проверить только через подключение";
            }
        }
        catch (Exception ex)
        {
            ProxyGeoInfo = $"❌ Invalid • {ex.Message}";
            IsProxyGeoVisible = true;
            StatusText = "❌ Proxy is not reachable";
        }
    }

    private string GetCountryFlag(string countryCode)
    {
        if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2)
            return "🌍";

        // Convert country code to flag emoji
        int codePoint1 = 0x1F1E6 + (countryCode[0] - 'A');
        int codePoint2 = 0x1F1E6 + (countryCode[1] - 'A');
        
        return char.ConvertFromUtf32(codePoint1) + char.ConvertFromUtf32(codePoint2);
    }

    private bool ParseProxyInput(string input)
    {
        try
        {
            Log($"\n[PARSE] Input: {input}");
            input = input.Trim();
            
            // Формат: socks5://user:pass@ip:port или http://user:pass@ip:port
            if (input.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(input);
                _currentProxyType = input.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? "SOCKS5" : "HTTP";
                _currentProxyIp = uri.Host;
                _currentProxyPort = uri.Port.ToString();
                
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var userInfo = uri.UserInfo.Split(':');
                    _currentProxyUsername = userInfo.Length > 0 ? userInfo[0] : "";
                    _currentProxyPassword = userInfo.Length > 1 ? userInfo[1] : "";
                }
                else
                {
                    _currentProxyUsername = "";
                    _currentProxyPassword = "";
                }
                
                Log($"[PARSE] Type: {_currentProxyType}, IP: {_currentProxyIp}, Port: {_currentProxyPort}");
                Log($"[PARSE] User: {_currentProxyUsername}, Pass: {_currentProxyPassword}");
                return true;
            }
            // Формат: ip:port или ip:port:pass или ip:port:user:pass
            else
            {
                var parts = input.Split(':');
                Log($"[PARSE] Parts count: {parts.Length}");
                
                if (parts.Length >= 2)
                {
                    _currentProxyType = "SOCKS5";
                    _currentProxyIp = parts[0].Trim();
                    _currentProxyPort = parts[1].Trim();
                    
                    // Формат прокси:
                    // ip:port - без авторизации
                    // ip:port:password - только пароль (username пустой)
                    // ip:port:user:password - стандартный формат
                    if (parts.Length == 3)
                    {
                        // ip:port:password (username пустой)
                        _currentProxyUsername = "";
                        _currentProxyPassword = parts[2].Trim();
                        Log($"[PARSE] Format: IP:PORT:PASS (3 parts)");
                    }
                    else if (parts.Length == 4)
                    {
                        // ip:port:user:password (стандартный формат)
                        _currentProxyUsername = parts[2].Trim();
                        _currentProxyPassword = parts[3].Trim();
                        Log($"[PARSE] Format: IP:PORT:USER:PASS (4 parts)");
                    }
                    else if (parts.Length > 4)
                    {
                        // ip:port:user:password (где password содержит :)
                        _currentProxyUsername = parts[2].Trim();
                        // Объединяем все части после 3-й как пароль
                        _currentProxyPassword = string.Join(":", parts.Skip(3)).Trim();
                        Log($"[PARSE] Format: IP:PORT:USER:PASS (password contains :)");
                    }
                    else
                    {
                        _currentProxyUsername = "";
                        _currentProxyPassword = "";
                        Log($"[PARSE] Format: IP:PORT (no auth)");
                    }
                    
                    Log($"[PARSE] Type: {_currentProxyType}, IP: {_currentProxyIp}, Port: {_currentProxyPort}");
                    Log($"[PARSE] User: '{_currentProxyUsername}', Pass: '{_currentProxyPassword}'");
                    
                    // Проверяем, что IP и порт валидные
                    if (string.IsNullOrWhiteSpace(_currentProxyIp))
                        return false;
                    
                    if (!ushort.TryParse(_currentProxyPort, out _))
                        return false;
                    
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[PARSE] ERROR: {ex.Message}\n{ex.StackTrace}");
            return false;
        }

        return false;
    }

    private void UpdateStats(object? sender, EventArgs e)
    {
        // Генерируем случайную статистику для демонстрации
        var uploadKb = _random.Next(100, 1000);
        var downloadKb = _random.Next(500, 5000);

        UploadSpeed = $"{uploadKb / 1000.0:F2} MB/s";
        DownloadSpeed = $"{downloadKb / 1000.0:F2} MB/s";

        // Случайный пинг
        Ping = $"{_random.Next(20, 150)} ms";
    }

    private void UpdateConnectionTime(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _connectionStartTime;
        ConnectionTime = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private async Task LoadProxyList()
    {
        if (_mainWindow == null)
            return;

        try
        {
            var storageProvider = _mainWindow.StorageProvider;
            
            var filePickerOptions = new FilePickerOpenOptions
            {
                Title = "Select Proxy List File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Text Files")
                    {
                        Patterns = new[] { "*.txt" }
                    },
                    new FilePickerFileType("All Files")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            };

            var files = await storageProvider.OpenFilePickerAsync(filePickerOptions);

            if (files.Count > 0)
            {
                var file = files[0];
                using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                
                LoadedProxyList.Clear();
                
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        LoadedProxyList.Add(line.Trim());
                    }
                }

                StatusText = $"✅ Loaded {LoadedProxyList.Count} proxies";
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"❌ Error loading file: {ex.Message}";
        }
    }

    private void SelectProxyFromHistory(string? proxy)
    {
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            ProxyInputText = proxy;
        }
    }

    // ========================================================================
    // РАЗВЕТВЛЕНИЕ (per-app proxy)
    // ========================================================================
    private void RefreshAvailableProxies()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        AvailableProxies.Clear();
        foreach (var p in ProxyHistory)
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) AvailableProxies.Add(p);
        foreach (var p in LoadedProxyList)
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) AvailableProxies.Add(p);
    }

    private async Task BrowseExeForMapping()
    {
        if (_mainWindow == null)
            return;

        try
        {
            var options = new FilePickerOpenOptions
            {
                Title = "Select a program (.exe)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            };

            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(options);
            if (files.Count > 0)
            {
                var name = files[0].Name; // e.g. chrome.exe
                NewMappingProcess = name;
            }
        }
        catch (Exception ex)
        {
            BranchingStatus = $"❌ File selection error: {ex.Message}";
        }
    }

    private void AddMapping()
    {
        var process = (NewMappingProcess ?? "").Trim();
        var proxy = (NewMappingProxy ?? "").Trim();

        if (string.IsNullOrWhiteSpace(process))
        {
            BranchingStatus = "❌ Specify a program (.exe)";
            return;
        }
        if (string.IsNullOrWhiteSpace(proxy))
        {
            BranchingStatus = "❌ Specify a proxy for the program";
            return;
        }
        if (!TryParseProxy(proxy, out _))
        {
            BranchingStatus = "❌ Invalid proxy format. Use socks5://user:pass@ip:port or ip:port:user:pass";
            return;
        }

        // Одна программа — один прокси: заменяем, если уже есть
        var existing = BranchingMappings.FirstOrDefault(
            m => string.Equals(m.ProcessName, process, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            BranchingMappings.Remove(existing);

        BranchingMappings.Add(new AppProxyMapping { ProcessName = process, ProxyString = proxy });

        NewMappingProcess = "";
        NewMappingProxy = "";
        BranchingStatus = $"✅ Added: {process}";
        SaveSettings();
    }

    private void RemoveMapping(AppProxyMapping? mapping)
    {
        if (mapping == null)
            return;
        BranchingMappings.Remove(mapping);
        BranchingStatus = $"🗑 Removed: {mapping.ProcessName}";
        SaveSettings();
    }

    private async Task ToggleBranching()
    {
        if (_isBranchingActive)
        {
            StopBranching();
        }
        else
        {
            await StartBranching();
        }
    }

    private async Task StartBranching()
    {
        if (_proxyService == null)
        {
            BranchingStatus = "Service not initialized";
            return;
        }
        if (_isProxyActive)
        {
            BranchingStatus = "❌ Disconnect the regular proxy on the Dashboard tab first";
            return;
        }
        if (BranchingMappings.Count == 0)
        {
            BranchingStatus = "❌ Add at least one program with a proxy";
            return;
        }

        try
        {
            Log("\n=== STARTING BRANCHING (per-app proxy) ===");
            _proxyService.ClearProxies();
            _branchingRuleIds.Clear();

            // Регистрируем прокси (одинаковые — переиспользуем один proxy_id)
            var proxyIdCache = new System.Collections.Generic.Dictionary<string, uint>();
            var proxyIps = new System.Collections.Generic.List<string>();

            foreach (var m in BranchingMappings)
            {
                if (string.IsNullOrWhiteSpace(m.ProcessName) || string.IsNullOrWhiteSpace(m.ProxyString))
                    continue;

                if (!proxyIdCache.ContainsKey(m.ProxyString))
                {
                    if (!TryParseProxy(m.ProxyString, out var pp))
                    {
                        BranchingStatus = $"❌ Invalid proxy for {m.ProcessName}";
                        _proxyService.ClearProxies();
                        return;
                    }

                    uint pid = _proxyService.AddProxy(pp.Type, pp.Ip, ushort.Parse(pp.Port), pp.User, pp.Pass);
                    if (pid == 0)
                    {
                        BranchingStatus = $"❌ Failed to register proxy {pp.Ip}:{pp.Port}";
                        _proxyService.ClearProxies();
                        return;
                    }
                    proxyIdCache[m.ProxyString] = pid;
                    if (!proxyIps.Contains(pp.Ip)) proxyIps.Add(pp.Ip);
                    Log($"[BRANCH] Proxy #{pid}: {pp.Type} {pp.Ip}:{pp.Port} (user='{pp.User}')");
                }
            }

            // Разветвление работает только по TCP (UDP relay поддерживает лишь глобальный прокси)
            _proxyService.SetDisableUdp(true);

            if (!_proxyService.Start())
            {
                BranchingStatus = "❌ Failed to start service (WinDivert). Run as Administrator!";
                _proxyService.ClearProxies();
                return;
            }

            // Локальные сети — напрямую
            _branchingRuleIds.Add(_proxyService.AddRule("*", "127.*.*.*", "*", "BOTH", "DIRECT"));
            _branchingRuleIds.Add(_proxyService.AddRule("*", "10.*.*.*", "*", "BOTH", "DIRECT"));
            _branchingRuleIds.Add(_proxyService.AddRule("*", "172.16.*.*-172.31.*.*", "*", "BOTH", "DIRECT"));
            _branchingRuleIds.Add(_proxyService.AddRule("*", "192.168.*.*", "*", "BOTH", "DIRECT"));
            _branchingRuleIds.Add(_proxyService.AddRule("*", "224.*.*.*", "*", "BOTH", "DIRECT"));
            _branchingRuleIds.Add(_proxyService.AddRule("*", "255.255.255.255", "*", "BOTH", "DIRECT"));

            // IP самих прокси — напрямую (защита от петли)
            foreach (var ip in proxyIps)
                _branchingRuleIds.Add(_proxyService.AddRule("*", ip, "*", "BOTH", "DIRECT"));

            // Правила: каждая программа → свой прокси
            int applied = 0;
            foreach (var m in BranchingMappings)
            {
                if (string.IsNullOrWhiteSpace(m.ProcessName) || !proxyIdCache.TryGetValue(m.ProxyString, out var pid))
                    continue;

                uint rid = _proxyService.AddRuleEx(m.ProcessName, "*", "*", "BOTH", "PROXY", pid);
                if (rid > 0)
                {
                    _branchingRuleIds.Add(rid);
                    applied++;
                    Log($"[BRANCH] Rule: {m.ProcessName} -> proxy #{pid}");
                }
            }

            // Всё остальное (не в списке) идёт напрямую — глобального PROXY-правила НЕ добавляем.

            IsBranchingActive = true;
            BranchingStatus = $"✅ Split tunneling active: {applied} program(s) proxied, all other traffic direct";
            Log("=== BRANCHING ACTIVE ===\n");
        }
        catch (Exception ex)
        {
            Log($"!!! EXCEPTION in StartBranching: {ex.Message}");
            BranchingStatus = $"❌ Error: {ex.Message}";
            try { _proxyService.ClearProxies(); _proxyService.Stop(); } catch { }
        }
    }

    private void StopBranching()
    {
        try
        {
            foreach (var id in _branchingRuleIds)
            {
                if (id > 0)
                {
                    try { _proxyService?.DeleteRule(id); } catch { }
                }
            }
            _branchingRuleIds.Clear();
            _proxyService?.ClearProxies();
            _proxyService?.Stop();
        }
        catch (Exception ex)
        {
            Log($"StopBranching error: {ex.Message}");
        }

        IsBranchingActive = false;
        BranchingStatus = "Split tunneling stopped.";
    }

    // Разбор строки прокси без изменения полей Dashboard
    private struct ParsedProxy { public string Type; public string Ip; public string Port; public string User; public string Pass; }

    private static bool TryParseProxy(string input, out ParsedProxy p)
    {
        p = new ParsedProxy { Type = "SOCKS5", Ip = "", Port = "", User = "", Pass = "" };
        try
        {
            input = (input ?? "").Trim();
            if (input.Length == 0)
                return false;

            if (input.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(input);
                p.Type = input.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? "SOCKS5" : "HTTP";
                p.Ip = uri.Host;
                p.Port = uri.Port.ToString();
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var ui = uri.UserInfo.Split(':');
                    p.User = ui.Length > 0 ? ui[0] : "";
                    p.Pass = ui.Length > 1 ? ui[1] : "";
                }
                return !string.IsNullOrWhiteSpace(p.Ip) && ushort.TryParse(p.Port, out _);
            }
            else
            {
                var parts = input.Split(':');
                if (parts.Length < 2)
                    return false;
                p.Type = "SOCKS5";
                p.Ip = parts[0].Trim();
                p.Port = parts[1].Trim();
                if (parts.Length == 3)
                {
                    p.User = "";
                    p.Pass = parts[2].Trim();
                }
                else if (parts.Length == 4)
                {
                    p.User = parts[2].Trim();
                    p.Pass = parts[3].Trim();
                }
                else if (parts.Length > 4)
                {
                    p.User = parts[2].Trim();
                    p.Pass = string.Join(":", parts.Skip(3)).Trim();
                }
                return !string.IsNullOrWhiteSpace(p.Ip) && ushort.TryParse(p.Port, out _);
            }
        }
        catch
        {
            return false;
        }
    }

    private void OpenTelegram()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/inter1ark",
                UseShellExecute = true
            });
        }
        catch
        {
            StatusText = "❌ Failed to open Telegram";
        }
    }

    private void LoadSettings()
    {
        try
        {
            var config = ConfigManager.LoadConfig();

            _minimizeToTray = config.CloseToTray;
            _startWithWindows = config.StartWithWindows;
            _autoConnectLastProxy = config.AutoConnectLastProxy;
            _showNotifications = config.ShowNotifications;
            _dnsBypass = !config.DnsViaProxy; // DnsBypass = true means DNS direct (NOT via proxy)
            _disableUdp = config.DisableUdp;

            // Load last proxy input
            if (!string.IsNullOrWhiteSpace(config.LastProxyInput))
            {
                _proxyInputText = config.LastProxyInput;
            }

            // Load proxy history
            ProxyHistory.Clear();
            if (config.ProxyHistory != null)
            {
                foreach (var proxy in config.ProxyHistory)
                {
                    ProxyHistory.Add(proxy);
                }
            }

            // Load proxy list
            LoadedProxyList.Clear();
            if (config.LoadedProxyList != null)
            {
                foreach (var proxy in config.LoadedProxyList)
                {
                    LoadedProxyList.Add(proxy);
                }
            }

            // Load branching mappings (program -> proxy)
            BranchingMappings.Clear();
            if (config.ProxyMappings != null)
            {
                foreach (var m in config.ProxyMappings)
                {
                    if (!string.IsNullOrWhiteSpace(m.ProcessName) && !string.IsNullOrWhiteSpace(m.ProxyString))
                        BranchingMappings.Add(m);
                }
            }

            // Check actual registry state for StartWithWindows
            _startWithWindows = IsAutoStartEnabled();

            Log($"Settings loaded: Tray={_minimizeToTray}, AutoStart={_startWithWindows}, AutoConnect={_autoConnectLastProxy}, LastProxy={_proxyInputText}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var config = new AppConfig
            {
                CloseToTray = _minimizeToTray,
                StartWithWindows = _startWithWindows,
                AutoConnectLastProxy = _autoConnectLastProxy,
                ShowNotifications = _showNotifications,
                DnsViaProxy = !_dnsBypass,
                DisableUdp = _disableUdp,
                LastProxyInput = _proxyInputText ?? "",
                ProxyHistory = ProxyHistory.ToList(),
                LoadedProxyList = LoadedProxyList.ToList(),
                ProxyMappings = BranchingMappings.ToList(),
            };

            ConfigManager.SaveConfig(config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    private void UpdateAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue("ProxyBridge", $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue("ProxyBridge", false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update autostart: {ex.Message}");
        }
    }

    private bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("ProxyBridge") != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task AutoConnectIfNeeded()
    {
        if (_autoConnectLastProxy && !string.IsNullOrWhiteSpace(_proxyInputText) && _proxyService != null)
        {
            Log($"Auto-connecting to last proxy: {_proxyInputText}");
            // Need to parse first
            if (ParseProxyInput(_proxyInputText))
            {
                await ConnectProxy();
            }
        }
    }
}
