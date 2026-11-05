using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZenithSniffer.Net;

/// <summary>
/// Minimal DNS client (A records over UDP). Needed because the hosts file points the game domains
/// at the proxy – but the proxy itself must reach the real servers.
/// </summary>
public sealed class UpstreamDns(IEnumerable<string> servers, IReadOnlyDictionary<string, string> overrides)
{
    private readonly List<IPAddress> _servers = servers.Select(IPAddress.Parse).ToList();
    private readonly ConcurrentDictionary<string, (IPAddress Ip, DateTime Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IPAddress> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        if (overrides.TryGetValue(host, out var o)) return IPAddress.Parse(o);
        if (_cache.TryGetValue(host, out var c) && c.Expires > DateTime.UtcNow) return c.Ip;

        Exception? last = null;
        foreach (var server in _servers)
        {
            try
            {
                var (ip, ttl) = await QueryAsync(server, host, ct);
                _cache[host] = (ip, DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 30, 3600)));
                return ip;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new IOException($"DNS resolution for {host} failed", last);
    }

    private static async Task<(IPAddress, int)> QueryAsync(IPAddress server, string host, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var q = new List<byte>();
        q.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            q.Add((byte)b.Length);
            q.AddRange(b);
        }
        q.AddRange([0, 0, 1, 0, 1]); // root, QTYPE A, QCLASS IN

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        await udp.SendAsync(q.ToArray(), new IPEndPoint(server, 53), cts.Token);
        var res = await udp.ReceiveAsync(cts.Token);
        var d = res.Buffer;
        if (d.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(d) != id) throw new IOException("Invalid DNS response");
        var qd = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        var an = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6));
        var p = 12;
        for (var i = 0; i < qd; i++) { p = SkipName(d, p); p += 4; }
        for (var i = 0; i < an; i++)
        {
            p = SkipName(d, p);
            var type = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
            var ttl = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 4));
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 8));
            p += 10;
            if (type == 1 && len == 4) return (new IPAddress(d.AsSpan(p, 4)), ttl);
            p += len;
        }
        throw new IOException($"No A record for {host}");
    }

    private static int SkipName(byte[] d, int p)
    {
        while (true)
        {
            var len = d[p];
            if (len == 0) return p + 1;
            if ((len & 0xC0) == 0xC0) return p + 2;
            p += 1 + len;
        }
    }
}
