using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;

namespace ProxyBridge.GUI.Interop;

public static class ProxyBridgeNative
{
    private const string DllName = "ProxyBridgeCore.dll";

    static ProxyBridgeNative()
    {
        var assemblyPath = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(assemblyPath))
        {
            var dllPath = Path.Combine(assemblyPath, DllName);
            if (File.Exists(dllPath))
            {
                NativeLibrary.Load(dllPath);
            }
        }
    }

    public enum ProxyType
    {
        HTTP = 0,
        SOCKS5 = 1
    }

    public enum RuleAction
    {
        PROXY = 0,
        DIRECT = 1,
        BLOCK = 2
    }

    public enum RuleProtocol
    {
        TCP = 0,
        UDP = 1,
        BOTH = 2
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ConnectionCallback(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
        uint pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destIp,
        ushort destPort,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string proxyInfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ProxyBridge_AddRule(
        [MarshalAs(UnmanagedType.LPStr)] string processName,
        [MarshalAs(UnmanagedType.LPStr)] string targetHosts,
        [MarshalAs(UnmanagedType.LPStr)] string targetPorts,
        RuleProtocol protocol,
        RuleAction action);

    // === Per-app "branching" mode ===
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ProxyBridge_AddProxy(
        ProxyType type,
        [MarshalAs(UnmanagedType.LPStr)] string proxyIp,
        ushort proxyPort,
        [MarshalAs(UnmanagedType.LPStr)] string username,
        [MarshalAs(UnmanagedType.LPStr)] string password);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_ClearProxies();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ProxyBridge_AddRuleEx(
        [MarshalAs(UnmanagedType.LPStr)] string processName,
        [MarshalAs(UnmanagedType.LPStr)] string targetHosts,
        [MarshalAs(UnmanagedType.LPStr)] string targetPorts,
        RuleProtocol protocol,
        RuleAction action,
        uint proxyId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_EnableRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_DisableRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_DeleteRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_EditRule(
        uint ruleId,
        [MarshalAs(UnmanagedType.LPStr)] string processName,
        [MarshalAs(UnmanagedType.LPStr)] string targetHosts,
        [MarshalAs(UnmanagedType.LPStr)] string targetPorts,
        RuleProtocol protocol,
        RuleAction action);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_SetProxyConfig(
        ProxyType type,
        [MarshalAs(UnmanagedType.LPStr)] string proxyIp,
        ushort proxyPort,
        [MarshalAs(UnmanagedType.LPStr)] string username,
        [MarshalAs(UnmanagedType.LPStr)] string password);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetLogCallback(LogCallback callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetConnectionCallback(ConnectionCallback callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetDnsViaProxy([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetDisableUdp([MarshalAs(UnmanagedType.Bool)] bool disable);

    /// <summary>0 direct, 1 via SOCKS5 UDP (blocked when the proxy cannot), 2 blocked.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetUdpMode(int mode);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetDnsProxy(uint proxyId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_SetDnsServer(string ipv4);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_Start();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_Stop();

    /// <summary>Why the last ProxyBridge_Start failed (UTF-8, empty on success); absent in cores before 3.6.2.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ProxyBridge_GetLastError(byte[] buffer, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ProxyBridge_TestConnection(
        [MarshalAs(UnmanagedType.LPStr)] string targetHost,
        ushort targetPort,
        [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder resultBuffer,
        UIntPtr bufferSize);

    /// <summary>Cumulative relayed TCP bytes since the last ProxyBridge_Start (sent = app to proxy).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_GetTrafficStats(out ulong bytesUp, out ulong bytesDown);
}
