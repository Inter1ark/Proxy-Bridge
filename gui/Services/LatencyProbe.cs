using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// Real latency to the proxy server: time to complete a TCP handshake with host:port.
/// No data is sent through the proxy, so the probe does not depend on credentials.
/// While interception is active the proxy's own address is excluded from capture,
/// so the measurement reflects the direct path to the proxy.
/// </summary>
public static class LatencyProbe
{
    /// <summary>Returns the connect time in milliseconds, or null if the proxy is unreachable or the timeout expires.</summary>
    public static async Task<int?> MeasureAsync(string host, int port, int timeoutMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);

            // Resolve first so DNS time is not counted as latency
            IPAddress? address;
            if (!IPAddress.TryParse(host, out address))
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? (addresses.Length > 0 ? addresses[0] : null);
                if (address == null)
                    return null;
            }

            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            sw.Stop();
            try { socket.Shutdown(SocketShutdown.Both); } catch { /* already closed by the peer */ }
            return (int)Math.Max(1, Math.Round(sw.Elapsed.TotalMilliseconds));
        }
        catch
        {
            return null;
        }
    }
}
