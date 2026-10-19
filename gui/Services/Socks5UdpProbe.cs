using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// Does a SOCKS5 proxy relay UDP? The probe opens UDP ASSOCIATE and sends a STUN Binding
/// Request through the relay; the answer carries the IP the world sees for the proxy's UDP
/// side (what WebRTC would reveal). No third-party library: the handshake is a few bytes.
/// </summary>
public static class Socks5UdpProbe
{
    public sealed record Result(bool Supported, string Detail, string Ip);

    private static readonly (string Host, int Port)[] StunServers =
    {
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
        ("stun1.l.google.com", 19302),
    };

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(4);

    public static async Task<Result> ProbeAsync(ParsedProxy p, CancellationToken ct = default)
    {
        if (p.IsHttp) return new Result(false, "http proxies cannot relay UDP", "");
        if (!int.TryParse(p.Port, out var port)) return new Result(false, "invalid port", "");
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(Timeout);
            await tcp.ConnectAsync(p.Host, port, connectCts.Token);
            var stream = tcp.GetStream();
            stream.ReadTimeout = (int)Timeout.TotalMilliseconds;
            stream.WriteTimeout = (int)Timeout.TotalMilliseconds;

            // greeting
            await stream.WriteAsync(p.HasAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
            var hello = await ReadExactAsync(stream, 2, ct);
            if (hello == null || hello[0] != 5) return new Result(false, "not a SOCKS5 proxy", "");
            if (hello[1] == 2)
            {
                var u = Encoding.UTF8.GetBytes(p.User);
                var w = Encoding.UTF8.GetBytes(p.Pass);
                var auth = new byte[3 + u.Length + w.Length];
                auth[0] = 1; auth[1] = (byte)u.Length; u.CopyTo(auth, 2);
                auth[2 + u.Length] = (byte)w.Length; w.CopyTo(auth, 3 + u.Length);
                await stream.WriteAsync(auth, ct);
                var ar = await ReadExactAsync(stream, 2, ct);
                if (ar == null || ar[1] != 0) return new Result(false, "auth failed", "");
            }
            else if (hello[1] != 0)
            {
                return new Result(false, $"auth method {hello[1]} not supported", "");
            }

            // UDP ASSOCIATE from any address/port
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            var rep = await ReadExactAsync(stream, 4, ct);
            if (rep == null) return new Result(false, "no reply to UDP ASSOCIATE", "");
            if (rep[1] != 0) return new Result(false, $"UDP ASSOCIATE refused (reply {rep[1]})", "");
            IPAddress relayIp;
            int relayPort;
            switch (rep[3])
            {
                case 1:
                    var a4 = await ReadExactAsync(stream, 6, ct);
                    if (a4 == null) return new Result(false, "short reply", "");
                    relayIp = new IPAddress(a4[..4]);
                    relayPort = (a4[4] << 8) | a4[5];
                    break;
                case 4:
                    var a6 = await ReadExactAsync(stream, 18, ct);
                    if (a6 == null) return new Result(false, "short reply", "");
                    relayIp = new IPAddress(a6[..16]);
                    relayPort = (a6[16] << 8) | a6[17];
                    break;
                case 3:
                    var ln = await ReadExactAsync(stream, 1, ct);
                    var dn = ln == null ? null : await ReadExactAsync(stream, ln[0] + 2, ct);
                    if (dn == null) return new Result(false, "short reply", "");
                    var name = Encoding.ASCII.GetString(dn, 0, ln![0]);
                    relayPort = (dn[ln[0]] << 8) | dn[ln[0] + 1];
                    relayIp = (await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, ct))[0];
                    break;
                default:
                    return new Result(false, "unknown address type in reply", "");
            }
            if (relayIp.Equals(IPAddress.Any) || relayIp.Equals(IPAddress.IPv6Any))
            {
                // RFC 1928 allows 0.0.0.0: use the address the TCP control connection went to
                relayIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            }

            using var udp = new UdpClient(relayIp.AddressFamily);
            var tid = RandomNumberGenerator.GetBytes(12);
            var stun = new byte[20];
            stun[0] = 0; stun[1] = 1;            // Binding Request
            stun[2] = 0; stun[3] = 0;            // length
            stun[4] = 0x21; stun[5] = 0x12; stun[6] = 0xA4; stun[7] = 0x42;
            tid.CopyTo(stun, 8);

            foreach (var (host, sport) in StunServers)
            {
                IPAddress sip;
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
                    if (addrs.Length == 0) continue;
                    sip = addrs[0];
                }
                catch
                {
                    continue;
                }
                var pkt = new byte[10 + stun.Length];
                pkt[3] = 1;
                sip.GetAddressBytes().CopyTo(pkt, 4);
                pkt[8] = (byte)(sport >> 8); pkt[9] = (byte)sport;
                stun.CopyTo(pkt, 10);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await udp.SendAsync(pkt, pkt.Length, new IPEndPoint(relayIp, relayPort));
                    using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rcts.CancelAfter(Timeout);
                    UdpReceiveResult r;
                    try
                    {
                        r = await udp.ReceiveAsync(rcts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        continue;
                    }
                    var ip = ParseStun(r.Buffer, tid);
                    if (ip != null) return new Result(true, $"stun {host}", ip);
                }
            }
            return new Result(false, "no STUN answer through the relay", "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, "");
        }
    }

    /// <summary>The mapped IP from a SOCKS5-wrapped STUN Binding Response, or null.</summary>
    internal static string? ParseStun(byte[] data, byte[] tid)
    {
        if (data.Length < 10 + 20 || data[2] != 0) return null;
        int off;
        switch (data[3])
        {
            case 1: off = 10; break;
            case 4: off = 22; break;
            case 3: off = 5 + data[4] + 2; break;
            default: return null;
        }
        if (data.Length < off + 20) return null;
        var body = new ReadOnlySpan<byte>(data, off, data.Length - off);
        if (body[0] != 1 || body[1] != 1) return null;          // Binding Success Response
        for (var i = 0; i < 12; i++) if (body[8 + i] != tid[i]) return null;
        int mlen = (body[2] << 8) | body[3];
        var pos = 20;
        var end = Math.Min(body.Length, 20 + mlen);
        while (pos + 4 <= end)
        {
            int at = (body[pos] << 8) | body[pos + 1];
            int al = (body[pos + 2] << 8) | body[pos + 3];
            if (pos + 4 + al > body.Length) break;
            var val = body.Slice(pos + 4, al);
            if (at == 0x0020 && al >= 8 && val[1] == 1)       // XOR-MAPPED-ADDRESS, IPv4
                return $"{val[4] ^ 0x21}.{val[5] ^ 0x12}.{val[6] ^ 0xA4}.{val[7] ^ 0x42}";
            if (at == 0x0001 && al >= 8 && val[1] == 1)       // MAPPED-ADDRESS, IPv4
                return $"{val[4]}.{val[5]}.{val[6]}.{val[7]}";
            pos += 4 + ((al + 3) & ~3);
        }
        return null;
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var got = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        while (got < n)
        {
            int r;
            try { r = await s.ReadAsync(buf.AsMemory(got, n - got), cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            if (r <= 0) return null;
            got += r;
        }
        return buf;
    }
}
