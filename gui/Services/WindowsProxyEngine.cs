using System;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>Thin adapter over <see cref="ProxyBridgeService"/>; behavior is unchanged.</summary>
public sealed class WindowsProxyEngine : IProxyEngine
{
    private readonly ProxyBridgeService _service;
    private bool _isRunning;
    private string _lastError = "";

    public WindowsProxyEngine()
    {
        _service = new ProxyBridgeService();
        _service.LogReceived += msg => LogReceived?.Invoke(msg);
    }

    /// <summary>The underlying native service, for callers that need the full API.</summary>
    public ProxyBridgeService Service => _service;

    public bool SupportsSplitTunnel => true;
    public bool ProvidesTrafficStats => true;
    public string LastError => _lastError;
    public bool IsRunning => _isRunning;

    public event Action<string>? LogReceived;
#pragma warning disable CS0067 // the native engine never stops on its own
    public event Action<string>? Stopped;
#pragma warning restore CS0067

    public bool SetProxyConfig(string type, string ip, ushort port, string username, string password)
        => _service.SetProxyConfig(type, ip, port, username, password);

    public void SetDisableUdp(bool disable) => _service.SetDisableUdp(disable);

    public void SetDnsViaProxy(bool enable) => _service.SetDnsViaProxy(enable);
    public void SetUdpMode(string mode) => _service.SetUdpMode(mode);
    public void SetDnsProxy(uint proxyId) => _service.SetDnsProxy(proxyId);

    public Task<bool> StartAsync()
    {
        _lastError = "";
        _isRunning = _service.Start();
        if (!_isRunning) _lastError = Localize(ProxyBridgeService.ReadLastError());
        return Task.FromResult(_isRunning);
    }

    /// <summary>Turns the core's "windivert &lt;code&gt; &lt;kind&gt; &lt;detail&gt;" into the user's language.</summary>
    internal static string Localize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(' ', 4);
        if (parts.Length < 3 || parts[0] != "windivert") return raw;
        var code = parts[1];
        var kind = parts[2];
        var detail = parts.Length > 3 ? parts[3] : "";
        var key = kind switch
        {
            "notfound" or "denied" or "blocked" or "inuse" or "stale" => "status.windivert." + kind,
            _ => "status.windivert.other",
        };
        return I18n.T(key, code, detail);
    }

    public bool Stop()
    {
        var ok = _service.Stop();
        if (ok) _isRunning = false;
        return ok;
    }

    public uint AddRule(string processName, string targetHosts, string targetPorts, string protocol, string action)
        => _service.AddRule(processName, targetHosts, targetPorts, protocol, action);

    public bool DeleteRule(uint ruleId) => _service.DeleteRule(ruleId);

    public uint AddProxy(string type, string ip, ushort port, string username, string password)
        => _service.AddProxy(type, ip, port, username, password);

    public void ClearProxies() => _service.ClearProxies();

    public uint AddRuleEx(string processName, string targetHosts, string targetPorts, string protocol, string action, uint proxyId)
        => _service.AddRuleEx(processName, targetHosts, targetPorts, protocol, action, proxyId);

    public bool TryGetTrafficStats(out long upBytes, out long downBytes)
    {
        upBytes = 0;
        downBytes = 0;
        try
        {
            ProxyBridge.GUI.Interop.ProxyBridgeNative.ProxyBridge_GetTrafficStats(out var up, out var down);
            upBytes = (long)up;
            downBytes = (long)down;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false; // older core library without counters
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}
