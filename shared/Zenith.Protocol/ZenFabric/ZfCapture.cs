using System.Text;

namespace Zenith.Protocol.ZenFabric;

public enum Direction : byte
{
    ClientToServer = 0,
    ServerToClient = 1,
}

/// <summary>
/// Binary capture format for DECRYPTED ZenFabric payloads.
///   Header: "ZFCAP1\0\0" (8 bytes)
///   Record: [int64 UnixMillis][u8 Direction][int32 SessionId][int32 Len][Payload = int32 OpCode + Body]
/// </summary>
public sealed class ZfCaptureWriter : IDisposable
{
    public static readonly byte[] Magic = "ZFCAP1\0\0"u8.ToArray();
    private readonly BinaryWriter _w;
    private readonly object _lock = new();

    public ZfCaptureWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _w = new BinaryWriter(fs, new UTF8Encoding(false), leaveOpen: false);
        _w.Write(Magic);
        _w.Flush();
    }

    public void Write(Direction dir, int sessionId, ReadOnlySpan<byte> payload, DateTimeOffset? time = null)
    {
        lock (_lock)
        {
            _w.Write((time ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
            _w.Write((byte)dir);
            _w.Write(sessionId);
            _w.Write(payload.Length);
            _w.Write(payload);
            _w.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) _w.Dispose();
    }
}

public sealed record ZfCaptureRecord(DateTimeOffset Time, Direction Direction, int SessionId, byte[] Payload);

public static class ZfCaptureReader
{
    public static IEnumerable<ZfCaptureRecord> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(fs);
        var magic = r.ReadBytes(8);
        if (!magic.AsSpan().SequenceEqual(ZfCaptureWriter.Magic))
            throw new InvalidDataException("Not a ZFCAP file");
        while (fs.Position < fs.Length)
        {
            ZfCaptureRecord rec;
            try
            {
                var t = r.ReadInt64();
                var d = (Direction)r.ReadByte();
                var s = r.ReadInt32();
                var n = r.ReadInt32();
                var p = r.ReadBytes(n);
                if (p.Length < n) yield break;
                rec = new ZfCaptureRecord(DateTimeOffset.FromUnixTimeMilliseconds(t), d, s, p);
            }
            catch (EndOfStreamException) { yield break; }
            yield return rec;
        }
    }
}
