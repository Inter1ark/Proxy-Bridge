using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class ProxySettingsViewModel : ViewModelBase
{
    private readonly Loc _loc = Loc.Instance;
    public Loc Loc => _loc;

    private string _proxyIp = "";
    private string _proxyPort = "";
    private string _proxyType = "SOCKS5";
    private string _proxyUsername = "";
    private string _proxyPassword = "";
    private string _ipError = "";
    private string _portError = "";
    private bool _isTestViewOpen = false;
    private string _testTargetHost = "google.com";
    private string _testTargetPort = "80";
    private string _testOutput = "";
    private bool _isTesting = false;
    private string _quickInput = "";
    private string _quickInputError = "";
    private Action<string, string, string, string, string>? _onSave;
    private Action? _onClose;
    private Services.ProxyBridgeService? _proxyService;

    // Список сохранённых прокси
    private List<SavedProxy> _savedProxies = new();
    private SavedProxy? _selectedProxy;

    public string QuickInput
    {
        get => _quickInput;
        set
        {
            SetProperty(ref _quickInput, value);
            QuickInputError = "";
        }
    }

    public string QuickInputError
    {
        get => _quickInputError;
        set => SetProperty(ref _quickInputError, value);
    }

    public List<SavedProxy> SavedProxies
    {
        get => _savedProxies;
        set => SetProperty(ref _savedProxies, value);
    }

    public SavedProxy? SelectedProxy
    {
        get => _selectedProxy;
        set
        {
            SetProperty(ref _selectedProxy, value);
            if (value != null)
            {
                LoadProxyFromSaved(value);
            }
        }
    }

    public string ProxyIp
    {
        get => _proxyIp;
        set
        {
            SetProperty(ref _proxyIp, value);
            IpError = "";
        }
    }

    public string ProxyPort
    {
        get => _proxyPort;
        set
        {
            SetProperty(ref _proxyPort, value);
            PortError = "";
        }
    }

    public string ProxyType
    {
        get => _proxyType;
        set => SetProperty(ref _proxyType, value);
    }

    public string ProxyUsername
    {
        get => _proxyUsername;
        set => SetProperty(ref _proxyUsername, value);
    }

    public string ProxyPassword
    {
        get => _proxyPassword;
        set => SetProperty(ref _proxyPassword, value);
    }

    public string IpError
    {
        get => _ipError;
        set => SetProperty(ref _ipError, value);
    }

    public string PortError
    {
        get => _portError;
        set => SetProperty(ref _portError, value);
    }

    public bool IsTestViewOpen
    {
        get => _isTestViewOpen;
        set => SetProperty(ref _isTestViewOpen, value);
    }

    public string TestTargetHost
    {
        get => _testTargetHost;
        set => SetProperty(ref _testTargetHost, value);
    }

    public string TestTargetPort
    {
        get => _testTargetPort;
        set => SetProperty(ref _testTargetPort, value);
    }

    public string TestOutput
    {
        get => _testOutput;
        set => SetProperty(ref _testOutput, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenTestCommand { get; }
    public ICommand CloseTestCommand { get; }
    public ICommand StartTestCommand { get; }
    public ICommand ParseQuickInputCommand { get; }
    public ICommand QuickTestCommand { get; }
    public ICommand SaveToListCommand { get; }
    public ICommand DeleteProxyCommand { get; }

    private bool IsValidIpOrDomain(string input)
    {
        if (IPAddress.TryParse(input, out _))
        {
            return true;
        }

        var domainRegex = new Regex(@"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?$");
        return domainRegex.IsMatch(input);
    }

    private void LoadProxyFromSaved(SavedProxy proxy)
    {
        ProxyType = proxy.Type;
        ProxyIp = proxy.Ip;
        ProxyPort = proxy.Port;
        ProxyUsername = proxy.Username ?? "";
        ProxyPassword = proxy.Password ?? "";
    }

    private void LoadSavedProxies()
    {
        try
        {
            var path = GetProxiesFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var proxies = JsonSerializer.Deserialize<List<SavedProxy>>(json);
                if (proxies != null)
                {
                    SavedProxies = proxies;
                }
            }
        }
        catch { }
    }

    private void SaveProxiesToFile()
    {
        try
        {
            var path = GetProxiesFilePath();
            var directory = Path.GetDirectoryName(path);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(SavedProxies, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private string GetProxiesFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProxyBridge",
            "proxies.json");
    }

    public ProxySettingsViewModel(string initialType, string initialIp, string initialPort, 
        string initialUsername, string initialPassword, 
        Action<string, string, string, string, string> onSave, Action onClose, 
        Services.ProxyBridgeService? proxyService)
    {
        _onSave = onSave;
        _onClose = onClose;
        _proxyService = proxyService;

        ProxyType = initialType;
        ProxyIp = initialIp;
        ProxyPort = initialPort;
        ProxyUsername = initialUsername;
        ProxyPassword = initialPassword;

        LoadSavedProxies();

        ParseQuickInputCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(QuickInput))
            {
                QuickInputError = "Enter proxy in format: ip:port:user:pass or ip:port";
                return;
            }

            // Поддержка форматов:
            // ip:port
            // ip:port:user:pass
            // socks5://ip:port
            // http://ip:port
            // socks5://user:pass@ip:port
            var parts = QuickInput.Trim().Split(':');
            
            string type = "SOCKS5";
            string ip = "";
            string port = "";
            string user = "";
            string pass = "";

            // Проверка на протокол в начале
            if (QuickInput.Contains("://"))
            {
                var protocolSplit = QuickInput.Split(new[] { "://" }, StringSplitOptions.None);
                if (protocolSplit.Length == 2)
                {
                    type = protocolSplit[0].ToUpper() == "HTTP" ? "HTTP" : "SOCKS5";
                    var rest = protocolSplit[1];

                    // Проверка на user:pass@ip:port
                    if (rest.Contains("@"))
                    {
                        var authSplit = rest.Split('@');
                        if (authSplit.Length == 2)
                        {
                            var authParts = authSplit[0].Split(':');
                            if (authParts.Length == 2)
                            {
                                user = authParts[0];
                                pass = authParts[1];
                            }
                            var hostParts = authSplit[1].Split(':');
                            if (hostParts.Length == 2)
                            {
                                ip = hostParts[0];
                                port = hostParts[1];
                            }
                        }
                    }
                    else
                    {
                        var hostParts = rest.Split(':');
                        if (hostParts.Length >= 2)
                        {
                            ip = hostParts[0];
                            port = hostParts[1];
                            if (hostParts.Length >= 4)
                            {
                                user = hostParts[2];
                                pass = hostParts[3];
                            }
                        }
                    }
                }
            }
            else if (parts.Length >= 2)
            {
                ip = parts[0];
                port = parts[1];
                if (parts.Length >= 4)
                {
                    user = parts[2];
                    pass = parts[3];
                }
            }

            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(port))
            {
                QuickInputError = "Invalid format. Use: ip:port:user:pass or socks5://user:pass@ip:port";
                return;
            }

            ProxyType = type;
            ProxyIp = ip;
            ProxyPort = port;
            ProxyUsername = user;
            ProxyPassword = pass;
            QuickInput = "";
            QuickInputError = "";
        });

        QuickTestCommand = new RelayCommand(async () =>
        {
            if (IsTesting) return;

            if (string.IsNullOrWhiteSpace(ProxyIp) || string.IsNullOrWhiteSpace(ProxyPort))
            {
                QuickInputError = "Fill proxy settings first";
                return;
            }

            IsTesting = true;
            QuickInputError = "Testing...";

            try
            {
                if (_proxyService != null && ushort.TryParse(ProxyPort, out ushort portNum))
                {
                    _proxyService.SetProxyConfig(ProxyType, ProxyIp, portNum, ProxyUsername ?? "", ProxyPassword ?? "");
                    
                    var result = await System.Threading.Tasks.Task.Run(() =>
                        _proxyService.TestConnection("google.com", 80));
                    
                    QuickInputError = result.Contains("SUCCESS") ? " Proxy works!" : " " + result;
                }
                else
                {
                    QuickInputError = "Invalid port number";
                }
            }
            catch (Exception ex)
            {
                QuickInputError = " Error: " + ex.Message;
            }
            finally
            {
                IsTesting = false;
            }
        });

        SaveToListCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(ProxyIp) || string.IsNullOrWhiteSpace(ProxyPort))
            {
                QuickInputError = "Fill proxy settings first";
                return;
            }

            var newProxy = new SavedProxy
            {
                Name = $"{ProxyIp}:{ProxyPort}",
                Type = ProxyType,
                Ip = ProxyIp,
                Port = ProxyPort,
                Username = ProxyUsername,
                Password = ProxyPassword,
                AddedDate = DateTime.Now
            };

            // Проверка на дубликаты
            if (!SavedProxies.Any(p => p.Ip == ProxyIp && p.Port == ProxyPort))
            {
                SavedProxies.Add(newProxy);
                SaveProxiesToFile();
                OnPropertyChanged(nameof(SavedProxies));
                QuickInputError = " Saved to list";
            }
            else
            {
                QuickInputError = "Already in list";
            }
        });

        DeleteProxyCommand = new RelayCommand(() =>
        {
            if (SelectedProxy != null)
            {
                SavedProxies.Remove(SelectedProxy);
                SaveProxiesToFile();
                OnPropertyChanged(nameof(SavedProxies));
                SelectedProxy = null;
            }
        });

        SaveCommand = new RelayCommand(() =>
        {
            bool isValid = true;

            if (string.IsNullOrWhiteSpace(ProxyIp))
            {
                IpError = "IP address or hostname is required";
                isValid = false;
            }
            else if (!IsValidIpOrDomain(ProxyIp))
            {
                IpError = "Invalid IP address or hostname";
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(ProxyPort))
            {
                PortError = "Port is required";
                isValid = false;
            }
            else if (!int.TryParse(ProxyPort, out int port) || port < 1 || port > 65535)
            {
                PortError = "Port must be between 1 and 65535";
                isValid = false;
            }

            if (isValid)
            {
                _onSave?.Invoke(ProxyType, ProxyIp, ProxyPort, ProxyUsername ?? "", ProxyPassword ?? "");
            }
        });

        CancelCommand = new RelayCommand(() =>
        {
            _onClose?.Invoke();
        });

        OpenTestCommand = new RelayCommand(() =>
        {
            IsTestViewOpen = true;
            TestOutput = "";
        });

        CloseTestCommand = new RelayCommand(() =>
        {
            IsTestViewOpen = false;
        });

        StartTestCommand = new RelayCommand(async () =>
        {
            if (IsTesting) return;

            if (string.IsNullOrWhiteSpace(ProxyIp))
            {
                TestOutput = "ERROR: Please configure proxy IP address or hostname first";
                return;
            }

            if (string.IsNullOrWhiteSpace(ProxyPort) || !ushort.TryParse(ProxyPort, out ushort proxyPortNum))
            {
                TestOutput = "ERROR: Please configure valid proxy port first";
                return;
            }

            if (string.IsNullOrWhiteSpace(TestTargetHost))
            {
                TestOutput = "ERROR: Please enter target host";
                return;
            }

            if (!ushort.TryParse(TestTargetPort, out ushort targetPortNum))
            {
                TestOutput = "ERROR: Invalid target port";
                return;
            }

            IsTesting = true;
            TestOutput = "Testing connection...\n";

            try
            {
                if (_proxyService != null)
                {
                    _proxyService.SetProxyConfig(ProxyType, ProxyIp, proxyPortNum, ProxyUsername ?? "", ProxyPassword ?? "");
                    
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var result = _proxyService.TestConnection(TestTargetHost, targetPortNum);
                        TestOutput = result;
                    });
                }
                else
                {
                    TestOutput = "ERROR: Proxy service not available";
                }
            }
            catch (Exception ex)
            {
                TestOutput += $"\nERROR: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        });
    }
}

public class SavedProxy
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "SOCKS5";
    public string Ip { get; set; } = "";
    public string Port { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public DateTime AddedDate { get; set; }
}
