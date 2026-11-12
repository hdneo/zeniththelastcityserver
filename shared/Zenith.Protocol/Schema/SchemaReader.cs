using System.Buffers.Binary;
using System.Text;

namespace Zenith.Protocol.Schema;

/// <summary>
/// Read counterpart of <see cref="SchemaWriter"/> (SchemaObject.ReadXxx / GetXxx in the client).
/// Note: the format is NOT self-describing. Without the schema you do not know the length of the
/// value after a FieldId – generic parsing is only possible with type information.
/// </summary>
public ref struct SchemaReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public SchemaReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;
    public bool HasMore => _pos < _data.Length;
    public ReadOnlySpan<byte> RemainingSpan => _data[_pos..];

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || n > Remaining)
            throw new SchemaFormatException($"Read error: need {n} bytes at position {_pos}, available {_data.Length - _pos}");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public byte[] ReadBytes()
    {
        var n = ReadInt32();
        return Take(n).ToArray();
    }

    public ReadOnlySpan<byte> ReadBytesSpan()
    {
        var n = ReadInt32();
        return Take(n);
    }

    public string ReadString()
    {
        var n = ReadInt32();
        return Encoding.UTF8.GetString(Take(n));
    }

    public ReadOnlySpan<byte> ReadRaw(int n) => Take(n);

    public uint ReadFieldId() => ReadUInt16();

    /// <summary>Peeks at the next FieldId without consuming it.</summary>
    public uint PeekFieldId() => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos, 2));

    // ---- Fields ----
    /// <summary>Reads the [HasValue] byte of an option field (after the FieldId).</summary>
    public bool ReadOptionHasValue() => ReadBool();

    public string? ReadOptionalString()
    {
        return ReadBool() ? ReadString() : null;
    }

    public int ReadListCount() => ReadInt32();

    public List<byte[]> ReadObjectList()
    {
        var n = ReadInt32();
        var list = new List<byte[]>(Math.Max(0, Math.Min(n, 4096)));
        for (var i = 0; i < n; i++) list.Add(ReadBytes());
        return list;
    }
}

public sealed class SchemaFormatException(string message) : Exception(message);
