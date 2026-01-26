using System;
using ProxyBridge.GUI.Interop;

namespace ProxyBridge.GUI.Services;

public class ProxyBridgeService : IDisposable
{
    private ProxyBridgeNative.LogCallback? _logCallback;
    private ProxyBridgeNative.ConnectionCallback? _connectionCallback;
    private bool _isRunning;
    private uint _proxyServerExclusionRuleId = 0; // Rule ID for excluding proxy server IP
    private string _currentProxyIp = "";

    public event Action<string>? LogReceived;
    public event Action<string, uint, string, ushort, string>? ConnectionReceived;

    public ProxyBridgeService()
    {
        _logCallback = OnLogReceived;
        _connectionCallback = OnConnectionReceived;

        ProxyBridgeNative.ProxyBridge_SetLogCallback(_logCallback);
        ProxyBridgeNative.ProxyBridge_SetConnectionCallback(_connectionCallback);
    }

    private void OnLogReceived(string message)
    {
        LogReceived?.Invoke(message);
    }

    private void OnConnectionReceived(string processName, uint pid, string destIp, ushort destPort, string proxyInfo)
    {
        ConnectionReceived?.Invoke(processName, pid, destIp, destPort, proxyInfo);
    }

    public bool Start()
    {
        if (_isRunning)
            return true;

        // ВАЖНО: НЕ создаем правило исключения автоматически!
        // Правило исключения для прокси-сервера должно создаваться ВРУЧНУЮ
        // в правильном порядке (перед wildcard * -> PROXY правилом)
        // См. MainWindowViewModel.ActivateAllTraffic()

        _isRunning = ProxyBridgeNative.ProxyBridge_Start();
        return _isRunning;
    }

    public bool Stop()
    {
        if (!_isRunning)
            return true;

        _isRunning = !ProxyBridgeNative.ProxyBridge_Stop();
        
        // Clean up exclusion rule
        if (_proxyServerExclusionRuleId > 0)
        {
            ProxyBridgeNative.ProxyBridge_DeleteRule(_proxyServerExclusionRuleId);
            _proxyServerExclusionRuleId = 0;
        }
        
        return !_isRunning;
    }

    public bool SetProxyConfig(string type, string ip, ushort port, string username, string password)
    {
        var proxyType = type.ToUpper() == "HTTP"
            ? ProxyBridgeNative.ProxyType.HTTP
            : ProxyBridgeNative.ProxyType.SOCKS5;

        bool success = ProxyBridgeNative.ProxyBridge_SetProxyConfig(proxyType, ip, port, username, password);
        
        if (success)
        {
            // CRITICAL: Store proxy IP for exclusion rule
            _currentProxyIp = ip;
            
            // ВАЖНО: НЕ создаем правило автоматически!
            // Правило создается вручную в MainWindowViewModel в правильном порядке
        }
        
        return success;
    }

    private void CreateProxyServerExclusionRule()
    {
        // Delete old exclusion rule if exists
        if (_proxyServerExclusionRuleId > 0)
        {
            ProxyBridgeNative.ProxyBridge_DeleteRule(_proxyServerExclusionRuleId);
            _proxyServerExclusionRuleId = 0;
        }

        if (string.IsNullOrEmpty(_currentProxyIp))
            return;

        // CRITICAL: Create DIRECT rule for proxy server IP for ALL processes (*)
        // This ensures ANY process (including relay server) can connect to proxy without being intercepted
        _proxyServerExclusionRuleId = ProxyBridgeNative.ProxyBridge_AddRule(
            "*",                 // ALL processes - wildcard to exclude proxy IP globally!
            _currentProxyIp,     // Target: proxy server IP only
            "*",                 // All ports
            ProxyBridgeNative.RuleProtocol.BOTH,  // TCP + UDP
            ProxyBridgeNative.RuleAction.DIRECT   // Allow direct connection - bypass proxy for proxy itself!
        );

        if (_proxyServerExclusionRuleId > 0)
        {
            LogReceived?.Invoke($"✅ КРИТИЧНО: Создано правило исключения для IP прокси-сервера: {_currentProxyIp} → DIRECT (Rule ID: {_proxyServerExclusionRuleId})");
            LogReceived?.Invoke($"   Все соединения к {_currentProxyIp} будут идти НАПРЯМУЮ (без перехвата WinDivert)");
        }
        else
        {
            LogReceived?.Invoke($"❌ ОШИБКА: Не удалось создать правило исключения для {_currentProxyIp}!");
        }
    }

    public uint AddRule(string processName, string targetHosts, string targetPorts, string protocol, string action)
    {
        var ruleAction = action.ToUpper() switch
        {
            "DIRECT" => ProxyBridgeNative.RuleAction.DIRECT,
            "BLOCK" => ProxyBridgeNative.RuleAction.BLOCK,
            _ => ProxyBridgeNative.RuleAction.PROXY
        };

        var ruleProtocol = protocol.ToUpper() switch
        {
            "UDP" => ProxyBridgeNative.RuleProtocol.UDP,
            "BOTH" => ProxyBridgeNative.RuleProtocol.BOTH,
            "TCP+UDP" => ProxyBridgeNative.RuleProtocol.BOTH,
            _ => ProxyBridgeNative.RuleProtocol.TCP
        };

        return ProxyBridgeNative.ProxyBridge_AddRule(processName, targetHosts, targetPorts, ruleProtocol, ruleAction);
    }

    public bool EnableRule(uint ruleId)
    {
        return ProxyBridgeNative.ProxyBridge_EnableRule(ruleId);
    }

    public bool DisableRule(uint ruleId)
    {
        return ProxyBridgeNative.ProxyBridge_DisableRule(ruleId);
    }

    public bool DeleteRule(uint ruleId)
    {
        return ProxyBridgeNative.ProxyBridge_DeleteRule(ruleId);
    }

    public bool EditRule(uint ruleId, string processName, string targetHosts, string targetPorts, string protocol, string action)
    {
        var ruleAction = action.ToUpper() switch
        {
            "DIRECT" => ProxyBridgeNative.RuleAction.DIRECT,
            "BLOCK" => ProxyBridgeNative.RuleAction.BLOCK,
            _ => ProxyBridgeNative.RuleAction.PROXY
        };

        var ruleProtocol = protocol.ToUpper() switch
        {
            "UDP" => ProxyBridgeNative.RuleProtocol.UDP,
            "BOTH" => ProxyBridgeNative.RuleProtocol.BOTH,
            "TCP+UDP" => ProxyBridgeNative.RuleProtocol.BOTH,
            _ => ProxyBridgeNative.RuleProtocol.TCP
        };

        return ProxyBridgeNative.ProxyBridge_EditRule(ruleId, processName, targetHosts, targetPorts, ruleProtocol, ruleAction);
    }

    public void SetDnsViaProxy(bool enable)
    {
        ProxyBridgeNative.ProxyBridge_SetDnsViaProxy(enable);
    }

    public void SetDisableUdp(bool disable)
    {
        ProxyBridgeNative.ProxyBridge_SetDisableUdp(disable);
    }

    public string TestConnection(string targetHost, ushort targetPort)
    {
        var buffer = new System.Text.StringBuilder(4096);
        int result = ProxyBridgeNative.ProxyBridge_TestConnection(
            targetHost,
            targetPort,
            buffer,
            (UIntPtr)buffer.Capacity);

        return buffer.ToString();
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            Stop();

            // WinDivert takes few seconds to stop
            System.Threading.Thread.Sleep(500);

            // STOP WinDivert kernel driver
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "stop WinDivert",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(2000);
            }
            catch {
             }
        }
        GC.SuppressFinalize(this);
    }
}
