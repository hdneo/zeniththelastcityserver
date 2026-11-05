using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ZenithSniffer.Capture;

/// <summary>
/// Readable packet log in the style of the MxO packet logs:
///
///   Client->Server [20:16:45.643 2026-11-04] Session 1  192.168.0.176:65301 -> 35.205.6.88:31257  size: 271  Op: 1 ProtocolSettings
///   Info: ProtocolSettings RSA-2048
///   01 00 00 00 00 01 00 00 ...                       (32 bytes per line)
///   ........ASCII.........                            (96 characters per line)
///   (blank line)
/// </summary>
public sealed class TextPacketLog : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();
    private readonly int _maxBytes;

    public TextPacketLog(string path, int maxBytesPerPacket)
    {
        _maxBytes = maxBytesPerPacket;
        _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(string direction, IPEndPoint? from, IPEndPoint? to, string? session, string? title, string? info, ReadOnlySpan<byte> data, DateTimeOffset? time = null)
    {
        var sb = new StringBuilder(data.Length * 4 + 256);
        sb.Append(direction).Append(" [").Append((time ?? DateTimeOffset.Now).ToLocalTime().ToString(CaptureSession.TimeFormat)).Append(']');
        if (session is not null) sb.Append(" Session ").Append(session);
        if (from is not null && to is not null) sb.Append("  ").Append(from).Append(" -> ").Append(to);
        sb.Append("  size: ").Append(data.Length);
        if (title is not null) sb.Append("  ").Append(title);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(info)) sb.Append("Info: ").AppendLine(info);

        var shown = _maxBytes > 0 && data.Length > _maxBytes ? data[.._maxBytes] : data;
        AppendHex(sb, shown);
        AppendAscii(sb, shown);
        if (shown.Length < data.Length)
            sb.Append("... ").Append(data.Length - shown.Length).AppendLine(" more bytes truncated (complete in .zfcap/.pcap)");
        sb.AppendLine();
        lock (_lock) _w.Write(sb.ToString());
    }

    public static void AppendHex(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("x2")).Append(' ');
            if (i % 32 == 31) sb.AppendLine();
        }
        if (data.Length % 32 != 0 || data.Length == 0) sb.AppendLine();
    }

    public static void AppendAscii(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            if (i % 96 == 95) sb.AppendLine();
        }
        if (data.Length % 96 != 0 || data.Length == 0) sb.AppendLine();
    }

    public void WriteRawText(string text)
    {
        lock (_lock) _w.Write(text);
    }

    public void Dispose()
    {
        lock (_lock) _w.Dispose();
    }
}

/// <summary>
/// pcap with synthetic IPv4/UDP packets (LINKTYPE_RAW), so the proxy's DECRYPTED
/// ZenFabric messages can be viewed in Wireshark.
/// Messages &gt; 65,000 bytes are split across several packets.
/// </summary>
public sealed class SyntheticPcapWriter : IDisposable
{
    private readonly BinaryWriter _w;
    private readonly object _lock = new();
    private ushort _ipId;
    private const int MaxChunk = 65000;

    public SyntheticPcapWriter(string path)
    {
        _w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        _w.Write(0xa1b2c3d4u); _w.Write((ushort)2); _w.Write((ushort)4);
        _w.Write(0); _w.Write(0u); _w.Write(65535u); _w.Write(101u);
        _w.Flush();
    }

    public void Write(IPEndPoint src, IPEndPoint dst, ReadOnlySpan<byte> payload, DateTimeOffset? time = null)
    {
        lock (_lock)
        {
            for (var off = 0; off < payload.Length || off == 0; off += MaxChunk)
            {
                var chunk = payload.Slice(off, Math.Min(MaxChunk, payload.Length - off));
                var pkt = new byte[28 + chunk.Length];
                pkt[0] = 0x45;
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), (ushort)pkt.Length);
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4), _ipId++);
                pkt[8] = 64; pkt[9] = 17;
                (src.Address.MapToIPv4()).GetAddressBytes().CopyTo(pkt, 12);
                (dst.Address.MapToIPv4()).GetAddressBytes().CopyTo(pkt, 16);
                uint sum = 0;
                for (var i = 0; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(i));
                while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(10), (ushort)~sum);
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(20), (ushort)src.Port);
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(22), (ushort)dst.Port);
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(24), (ushort)(8 + chunk.Length));
                chunk.CopyTo(pkt.AsSpan(28));

                var now = time ?? DateTimeOffset.UtcNow;
                _w.Write((uint)now.ToUnixTimeSeconds());
                _w.Write((uint)(now.Ticks % TimeSpan.TicksPerSecond / 10));
                _w.Write((uint)pkt.Length); _w.Write((uint)pkt.Length);
                _w.Write(pkt);
                if (payload.Length == 0) break;
            }
            _w.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) _w.Dispose();
    }
}
