using System;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// Platform-neutral surface used by the main view model. On Windows it is backed by the
/// native ProxyBridgeCore.dll (WinDivert); on macOS by the bundled pbcore helper.
/// </summary>
public interface IProxyEngine : IDisposable
{
    /// <summary>True when per-app rules (Split Tunnel) are available on this platform.</summary>
    bool SupportsSplitTunnel { get; }

    /// <summary>True when <see cref="TryGetTrafficStats"/> returns real byte counters.</summary>
    bool ProvidesTrafficStats { get; }

    /// <summary>Human readable reason of the last failed Start/Stop, or an empty string.</summary>
    string LastError { get; }

    bool IsRunning { get; }

    event Action<string>? LogReceived;

    /// <summary>Raised (on any thread) when the engine stops on its own, with a message.</summary>
    event Action<string>? Stopped;

    bool SetProxyConfig(string type, string ip, ushort port, string username, string password);
    void SetDisableUdp(bool disable);
    void SetDnsViaProxy(bool enable);

    Task<bool> StartAsync();
    bool Stop();

    uint AddRule(string processName, string targetHosts, string targetPorts, string protocol, string action);
    bool DeleteRule(uint ruleId);

    uint AddProxy(string type, string ip, ushort port, string username, string password);
    void ClearProxies();
    uint AddRuleEx(string processName, string targetHosts, string targetPorts, string protocol, string action, uint proxyId);

    bool TryGetTrafficStats(out long upBytes, out long downBytes);
}
