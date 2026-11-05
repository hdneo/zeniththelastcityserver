using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Zenith.Protocol.ZenFabric;
using ZenithSniffer.Capture;

namespace ZenithSniffer.Passive;

/// <summary>
/// Passive capture without touching the connection (Windows raw socket, SIO_RCVALL, admin required).
///  * detects LiteNetLib connections automatically (ConnectRequest, Zenith 69-byte ACK) or via --port
///  * writes all matching UDP packets as .pcap (for Wireshark, LINKTYPE_RAW) and udp-raw.log
///  * reassembles fragments and decodes the unencrypted handshake (ProtocolSettings, AuthWorker)
///
/// LIMIT: after the handshake the ZenFabric content is XOR-encrypted with a random per-session key
/// that is only transmitted RSA-protected. Use "proxy" mode for plain text.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PassiveCapture(IPAddress localIp, HashSet<int> ports, CaptureSession capture, bool logAcks = false)
{
    private sealed class Flow
    {
        public required IPEndPoint Client;
        public required IPEndPoint Server;
        public int Id;
        public bool Encrypted;
        public readonly Dictionary<(Direction, ushort), SortedDictionary<ushort, byte[]>> Fragments = new();
        public readonly Dictionary<Direction, HashSet<(byte, ushort)>> Seen = new() { [Direction.ClientToServer] = [], [Direction.ServerToClient] = [] };
    }

    private readonly Dictionary<string, Flow> _flows = new();
    private int _nextFlow;

    private static string Key(IPEndPoint a, IPEndPoint b) =>
        string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? $"{a}|{b}" : $"{b}|{a}";

    public void Run(CancellationToken ct)
    {
        using var pcap = new PcapWriter(Path.Combine(capture.Directory, "zenith-udp.pcap"));
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
        sock.Bind(new IPEndPoint(localIp, 0));
        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
        sock.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
        sock.ReceiveTimeout = 500;
        Console.WriteLine($"[PASSIVE] Listening on {localIp} (raw socket). Ports: {(ports.Count == 0 ? "automatic (connection detection)" : string.Join(",", ports))}");
        Console.WriteLine($"[PASSIVE] Output: {capture.Directory}");

        var buf = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = sock.Receive(buf); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
            if (n < 28 || (buf[0] >> 4) != 4 || buf[9] != 17) continue; // IPv4/UDP only
            var ihl = (buf[0] & 0x0F) * 4;
            var src = new IPEndPoint(new IPAddress(buf.AsSpan(12, 4)), BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(ihl)));
            var dst = new IPEndPoint(new IPAddress(buf.AsSpan(16, 4)), BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(ihl + 2)));
            var udpLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(ihl + 4));
            var data = buf.AsSpan(ihl + 8, Math.Min(udpLen - 8, n - ihl - 8)).ToArray();

            var key = Key(src, dst);
            if (!_flows.TryGetValue(key, out var flow))
            {
                var first = LnlPacket.Parse(data).FirstOrDefault();
                var byPort = ports.Contains(src.Port) || ports.Contains(dst.Port);
                // Zenith signature in the middle of a running session: 69-byte ACK (window size 512)
                var zenithAck = data.Length == 69 && (data[0] & 0x1F) == (byte)LnlProperty.Ack && data[3] == 2;
                if (!first.IsLiteNetLibConnectRequest && !byPort && !zenithAck) continue;
                bool clientIsSrc;
                if (first.IsLiteNetLibConnectRequest) clientIsSrc = true;
                else if (ports.Count > 0) clientIsSrc = ports.Contains(dst.Port);
                else clientIsSrc = src.Address.Equals(localIp);
                flow = new Flow { Client = clientIsSrc ? src : dst, Server = clientIsSrc ? dst : src, Id = ++_nextFlow };
                _flows[key] = flow;
                Console.WriteLine($"[PASSIVE] New LiteNetLib connection #{flow.Id}: {flow.Client} -> {flow.Server}");
                capture.LogSession(new { t = DateTimeOffset.UtcNow, evt = "passive-flow", flow = flow.Id, client = flow.Client.ToString(), server = flow.Server.ToString() });
                capture.RegisterEndpoints(flow.Id, flow.Client, flow.Server);
            }
            pcap.Write(buf.AsSpan(0, n));
            var dir = src.Equals(flow.Client) ? Direction.ClientToServer : Direction.ServerToClient;
            var parsed = LnlPacket.Parse(data).ToList();
            if (logAcks || parsed.Any(p => p.Property is not (LnlProperty.Ack or LnlProperty.Ping or LnlProperty.Pong or LnlProperty.MtuCheck or LnlProperty.MtuOk)))
                capture.LogRawUdp(dir == Direction.ClientToServer, src, dst,
                    $"Flow #{flow.Id}  LiteNetLib: " + string.Join(" | ", parsed.Select(p => p.Describe())) + (flow.Encrypted ? "  [ZenFabric encrypted]" : ""), data);
            foreach (var p in parsed) Handle(flow, dir, p);
        }
    }

    private void Handle(Flow f, Direction dir, LnlPacket p)
    {
        if (p.Property is LnlProperty.Disconnect or LnlProperty.ShutdownOk)
            Console.WriteLine($"[PASSIVE #{f.Id}] {p.Property}");
        if (p.Property != LnlProperty.Channeled) return;
        if (!f.Seen[dir].Add((p.ChannelId, p.Sequence))) return; // retransmission
        if (f.Seen[dir].Count > 30000) f.Seen[dir].Clear();

        byte[] message;
        if (p.Fragmented)
        {
            var k = (dir, p.FragmentId);
            if (!f.Fragments.TryGetValue(k, out var parts)) f.Fragments[k] = parts = new();
            parts[p.FragmentPart] = p.Payload.ToArray();
            if (parts.Count < p.FragmentsTotal) return;
            message = parts.Values.SelectMany(x => x).ToArray();
            f.Fragments.Remove(k);
        }
        else message = p.Payload.ToArray();

        if (message.Length < 8) return;
        if (f.Encrypted)
        {
            capture.LogZenFabric(dir, f.Id, BuildOpaque(message), "encrypted (not readable in passive mode)");
            return;
        }
        try
        {
            var payload = ZfCodec.Unframe(message, null);
            var ok = ZfOps.TryParse(payload, out var op, out _);
            if (ok && op is AuthWorkerOp) f.Encrypted = true;
            if (ok && op is ProtocolSettingsOp or AuthWorkerOp) capture.LogZenFabric(dir, f.Id, payload, "handshake (plain text)");
            else capture.LogZenFabric(dir, f.Id, BuildOpaque(message), "unknown");
        }
        catch
        {
            capture.LogZenFabric(dir, f.Id, BuildOpaque(message), "no ZenFabric frame");
        }
    }

    /// <summary>Wraps unreadable data as UnknownOp (opcode -1) so logs/zfcap stay consistent.</summary>
    private static byte[] BuildOpaque(byte[] raw)
    {
        var b = new byte[4 + raw.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, -1);
        raw.CopyTo(b, 4);
        return b;
    }

    /// <summary>Determines the local IP of the default route.</summary>
    public static IPAddress DefaultLocalIp()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect("1.1.1.1", 53);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}

/// <summary>Minimal pcap writer (LINKTYPE_RAW = 101, IPv4 packets without Ethernet header).</summary>
public sealed class PcapWriter : IDisposable
{
    private readonly BinaryWriter _w;

    public PcapWriter(string path)
    {
        _w = new BinaryWriter(File.Create(path));
        _w.Write(0xa1b2c3d4u); _w.Write((ushort)2); _w.Write((ushort)4);
        _w.Write(0); _w.Write(0u); _w.Write(65535u); _w.Write(101u);
    }

    public void Write(ReadOnlySpan<byte> packet)
    {
        var now = DateTimeOffset.UtcNow;
        _w.Write((uint)now.ToUnixTimeSeconds());
        _w.Write((uint)(now.Ticks % TimeSpan.TicksPerSecond / 10));
        _w.Write((uint)packet.Length); _w.Write((uint)packet.Length);
        _w.Write(packet);
        _w.Flush();
    }

    public void Dispose() => _w.Dispose();
}
