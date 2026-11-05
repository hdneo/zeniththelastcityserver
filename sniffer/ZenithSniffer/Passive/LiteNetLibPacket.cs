using System.Buffers.Binary;

namespace ZenithSniffer.Passive;

/// <summary>LiteNetLib 1.0 PacketProperty (lower 5 bits of the first byte).</summary>
public enum LnlProperty : byte
{
    Unreliable = 0, Channeled = 1, Ack = 2, Ping = 3, Pong = 4, ConnectRequest = 5, ConnectAccept = 6,
    Disconnect = 7, UnconnectedMessage = 8, MtuCheck = 9, MtuOk = 10, Broadcast = 11, Merged = 12,
    ShutdownOk = 13, PeerNotFound = 14, InvalidProtocol = 15, NatMessage = 16, Empty = 17,
}

/// <summary>
/// Header layout of LiteNetLib 1.0.x (ProtocolId 13) as built into the client:
///   Byte 0: Bits 0-4 Property, Bits 5-6 ConnectionNumber, Bit 7 Fragmented
///   Channeled/Ack: [1..2] Sequence (u16), [3] ChannelId (= channel*4 + DeliveryMethod; ReliableOrdered channel 0 = 2)
///   Fragmented:    [4..5] FragmentId, [6..7] FragmentPart, [8..9] FragmentsTotal, payload from 10
///   ConnectRequest:[1..4] ProtocolId (=13), [5..12] ConnectionTime, [13..16] PeerId, [17] AddrSize, Addr, key data
///   Merged:        [1..] repeated [u16 size][packet]
/// </summary>
public readonly record struct LnlPacket(LnlProperty Property, byte ConnectionNumber, bool Fragmented, ushort Sequence, byte ChannelId,
    ushort FragmentId, ushort FragmentPart, ushort FragmentsTotal, ReadOnlyMemory<byte> Payload, ReadOnlyMemory<byte> Raw)
{
    public const int ProtocolId = 13;

    public static IEnumerable<LnlPacket> Parse(ReadOnlyMemory<byte> data)
    {
        var s = data.Span;
        if (s.Length < 1) yield break;
        var prop = (LnlProperty)(s[0] & 0x1F);
        if (prop == LnlProperty.Merged)
        {
            var p = 1;
            while (p + 2 <= data.Length)
            {
                var size = BinaryPrimitives.ReadUInt16LittleEndian(data.Span[p..]);
                p += 2;
                if (size == 0 || p + size > data.Length) yield break;
                foreach (var inner in Parse(data.Slice(p, size))) yield return inner;
                p += size;
            }
            yield break;
        }
        var conn = (byte)((s[0] >> 5) & 0x03);
        var frag = (s[0] & 0x80) != 0;
        ushort seq = 0, fid = 0, fpart = 0, ftotal = 0;
        byte ch = 0;
        var header = prop switch
        {
            LnlProperty.Channeled or LnlProperty.Ack => 4,
            LnlProperty.Ping => 3,
            LnlProperty.Pong => 11,
            LnlProperty.ConnectRequest => 18,
            LnlProperty.ConnectAccept => 15,
            LnlProperty.Disconnect => 9,
            _ => 1,
        };
        if (prop is LnlProperty.Channeled or LnlProperty.Ack or LnlProperty.Ping or LnlProperty.Pong && s.Length >= 3)
            seq = BinaryPrimitives.ReadUInt16LittleEndian(s[1..]);
        if (prop is LnlProperty.Channeled or LnlProperty.Ack && s.Length >= 4)
            ch = s[3];
        if (frag && s.Length >= header + 6)
        {
            fid = BinaryPrimitives.ReadUInt16LittleEndian(s[(header)..]);
            fpart = BinaryPrimitives.ReadUInt16LittleEndian(s[(header + 2)..]);
            ftotal = BinaryPrimitives.ReadUInt16LittleEndian(s[(header + 4)..]);
            header += 6;
        }
        var payload = header <= data.Length ? data[header..] : ReadOnlyMemory<byte>.Empty;
        yield return new LnlPacket(prop, conn, frag, seq, ch, fid, fpart, ftotal, payload, data);
    }

    public bool IsLiteNetLibConnectRequest =>
        Property == LnlProperty.ConnectRequest && Raw.Length >= 18 && BinaryPrimitives.ReadInt32LittleEndian(Raw.Span[1..]) == ProtocolId;

    public string Describe() => Property switch
    {
        LnlProperty.Channeled => $"Channeled seq={Sequence} ch={ChannelId}{(Fragmented ? $" frag {FragmentId}:{FragmentPart + 1}/{FragmentsTotal}" : "")} {Payload.Length}B",
        LnlProperty.Ack => $"Ack seq={Sequence} ch={ChannelId}",
        LnlProperty.ConnectRequest => $"ConnectRequest protocol={(Raw.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(Raw.Span[1..]) : -1)}",
        _ => $"{Property} {Payload.Length}B",
    };
}
