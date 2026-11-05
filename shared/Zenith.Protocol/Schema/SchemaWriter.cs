using System.Buffers.Binary;
using System.Text;

namespace Zenith.Protocol.Schema;

/// <summary>
/// Re-implementation of Improbable.Worker.CInterop.SchemaObject (write side) from the Zenith client.
/// Everything little-endian, no varints, no type tags:
///   field    = [uint16 FieldId] [optional: 1 byte HasValue] [value]
///   string   = [int32 length] [UTF-8 bytes]
///   bytes    = [int32 length] [bytes]
///   object   = [int32 length] [bytes of a nested SchemaObject]
///   list     = [uint16 FieldId] [int32 count] [elements...]
/// </summary>
public sealed class SchemaWriter
{
    private byte[] _buf;
    private int _pos;

    public SchemaWriter(int capacity = 256) => _buf = new byte[Math.Max(16, capacity)];

    public int Length => _pos;

    public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();

    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _pos);

    private Span<byte> Reserve(int n)
    {
        if (_pos + n > _buf.Length)
        {
            var nb = new byte[Math.Max(_buf.Length * 2, _pos + n)];
            Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
            _buf = nb;
        }
        var s = _buf.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    // ---- Primitive (SchemaObject.WriteXxx) ----
    public void WriteByte(byte v) => Reserve(1)[0] = v;
    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);
    public void WriteInt16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), v);
    public void WriteUInt16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), v);
    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), v);
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void WriteInt64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), v);
    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);
    public void WriteFloat(float v) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), v);
    public void WriteDouble(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), v);

    public void WriteRaw(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    /// <summary>[int32 len][bytes]; null is written as length 0 (like the client).</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        WriteInt32(data.Length);
        WriteRaw(data);
    }

    public void WriteBytes(byte[]? data) => WriteBytes(data is null ? ReadOnlySpan<byte>.Empty : data.AsSpan());

    /// <summary>[int32 len][UTF-8]. Same as SchemaObject.WriteStringNotNull.</summary>
    public void WriteString(string? s)
    {
        s ??= string.Empty;
        var n = Encoding.UTF8.GetByteCount(s);
        WriteInt32(n);
        Encoding.UTF8.GetBytes(s, Reserve(n));
    }

    public void WriteFieldId(uint fieldId) => WriteUInt16((ushort)fieldId);

    // ---- Fields (SchemaObject.AddXxx) ----
    private void Head(uint fieldId, bool isOption)
    {
        WriteFieldId(fieldId);
        if (isOption) WriteByte(1);
    }

    public void AddEmptyOption(uint fieldId) { WriteFieldId(fieldId); WriteByte(0); }
    public void AddEmptyList(uint fieldId) { WriteFieldId(fieldId); WriteInt32(0); }
    public void AddBool(uint fieldId, bool v, bool isOption = false) { Head(fieldId, isOption); WriteBool(v); }
    public void AddInt32(uint fieldId, int v, bool isOption = false) { Head(fieldId, isOption); WriteInt32(v); }
    public void AddUInt32(uint fieldId, uint v, bool isOption = false) { Head(fieldId, isOption); WriteUInt32(v); }
    public void AddEnum(uint fieldId, uint v, bool isOption = false) => AddUInt32(fieldId, v, isOption);
    public void AddInt64(uint fieldId, long v, bool isOption = false) { Head(fieldId, isOption); WriteInt64(v); }
    public void AddUInt64(uint fieldId, ulong v, bool isOption = false) { Head(fieldId, isOption); WriteUInt64(v); }
    public void AddEntityId(uint fieldId, long v, bool isOption = false) => AddInt64(fieldId, v, isOption);
    public void AddFloat(uint fieldId, float v, bool isOption = false) { Head(fieldId, isOption); WriteFloat(v); }
    public void AddDouble(uint fieldId, double v, bool isOption = false) { Head(fieldId, isOption); WriteDouble(v); }
    public void AddBytes(uint fieldId, byte[]? v, bool isOption = false) { Head(fieldId, isOption); WriteBytes(v); }

    /// <summary>
    /// Strings are ALWAYS encoded as "optional" in the client: [FieldId][HasValue][string if HasValue].
    /// (SchemaObject.AddString writes the HasValue byte regardless of isOption.)
    /// </summary>
    public void AddString(uint fieldId, string? v)
    {
        WriteFieldId(fieldId);
        WriteBool(v is not null);
        if (v is not null) WriteString(v);
    }

    public void AddObject(uint fieldId, SchemaWriter obj, bool isOption = false)
    {
        Head(fieldId, isOption);
        WriteBytes(obj.WrittenSpan);
    }

    public void AddObject(uint fieldId, Action<SchemaWriter> build, bool isOption = false)
    {
        var w = new SchemaWriter();
        build(w);
        AddObject(fieldId, w, isOption);
    }

    public void AddObjectList(uint fieldId, IReadOnlyList<SchemaWriter> items)
    {
        WriteFieldId(fieldId);
        WriteInt32(items.Count);
        foreach (var it in items) WriteBytes(it.WrittenSpan);
    }

    public void AddInt64List(uint fieldId, IReadOnlyList<long> items)
    {
        WriteFieldId(fieldId);
        WriteInt32(items.Count);
        foreach (var it in items) WriteInt64(it);
    }

    public void AddUInt32List(uint fieldId, IReadOnlyList<uint> items)
    {
        WriteFieldId(fieldId);
        WriteInt32(items.Count);
        foreach (var it in items) WriteUInt32(it);
    }

    public void AddStringList(uint fieldId, IReadOnlyList<string> items)
    {
        WriteFieldId(fieldId);
        WriteInt32(items.Count);
        foreach (var it in items) WriteString(it);
    }

    /// <summary>map&lt;K,V&gt; is encoded as a list of objects {1: key, 2: value} (empty: AddEmptyList).</summary>
    public void AddMap<TK, TV>(uint fieldId, IEnumerable<KeyValuePair<TK, TV>> entries,
        Action<SchemaWriter, TK> writeKey, Action<SchemaWriter, TV> writeValue)
    {
        var list = new List<SchemaWriter>();
        foreach (var kv in entries)
        {
            var e = new SchemaWriter();
            writeKey(e, kv.Key);
            writeValue(e, kv.Value);
            list.Add(e);
        }
        if (list.Count == 0) AddEmptyList(fieldId);
        else AddObjectList(fieldId, list);
    }
}
