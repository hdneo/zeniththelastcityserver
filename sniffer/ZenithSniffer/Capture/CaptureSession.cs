using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Zenith.Protocol.ZenFabric;

namespace ZenithSniffer.Capture;

/// <summary>
/// One capture folder per sniffer run:
///   zenfabric.log            – READABLE: every ZenFabric message (decrypted) with hex and ASCII block (MxO style)
///   zenfabric-decrypted.pcap – the same messages for Wireshark (synthetic UDP packets)
///   zenfabric.jsonl          – every operation decoded (one JSON line per op)
///   zenfabric.zfcap          – all decrypted payloads, binary (for "decode")
///   http.log / http.jsonl    – all HTTP(S) requests/responses in plain text
///   sessions.jsonl           – connections incl. the symmetric key of each session
///   (passive mode also: zenith-udp.pcap + udp-raw.log with every UDP packet)
/// WARNING: contains session tickets/tokens of your own account – never share unfiltered, use "export".
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public const string TimeFormat = "HH:mm:ss.fff yyyy-MM-dd";

    private readonly StreamWriter _http, _zf, _sessions;
    private readonly ZfCaptureWriter _zfcap;
    private readonly TextPacketLog _zfText, _httpText;
    private readonly SyntheticPcapWriter _zfPcap;
    private TextPacketLog? _rawText;
    private readonly object _lock = new();
    private readonly SnifferOptions _opts;
    private readonly ConcurrentDictionary<int, (IPEndPoint Client, IPEndPoint Server)> _endpoints = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly IPEndPoint Unknown = new(IPAddress.Loopback, 0);

    public string Directory { get; }

    public CaptureSession(SnifferOptions opts, string? suffix = null, string? explicitDirectory = null)
    {
        _opts = opts;
        Directory = explicitDirectory is not null
            ? Path.GetFullPath(explicitDirectory)
            : Path.GetFullPath(Path.Combine(opts.OutputDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + (suffix is null ? "" : "-" + suffix)));
        System.IO.Directory.CreateDirectory(Directory);
        _http = Open("http.jsonl");
        _zf = Open("zenfabric.jsonl");
        _sessions = Open("sessions.jsonl");
        _zfcap = new ZfCaptureWriter(Path.Combine(Directory, "zenfabric.zfcap"));
        _zfText = new TextPacketLog(Path.Combine(Directory, "zenfabric.log"), opts.TextLogMaxBytes);
        _httpText = new TextPacketLog(Path.Combine(Directory, "http.log"), 0);
        _zfPcap = new SyntheticPcapWriter(Path.Combine(Directory, "zenfabric-decrypted.pcap"));
    }

    private StreamWriter Open(string name) =>
        new(new FileStream(Path.Combine(Directory, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>Client/server endpoints of a session (for the text log and pcap).</summary>
    public void RegisterEndpoints(int session, IPEndPoint client, IPEndPoint server) => _endpoints[session] = (client, server);

    public void LogHttp(object entry)
    {
        lock (_lock) _http.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>Readable HTTP entry (request + response).</summary>
    public void LogHttpText(string method, string url, IDictionary<string, string> reqHeaders, string reqBody, int status, IDictionary<string, string> respHeaders, string respBody, string? note, DateTimeOffset? time = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP [").Append((time ?? DateTimeOffset.Now).ToLocalTime().ToString(TimeFormat)).Append("] ").Append(method).Append(' ').Append(url).Append(" -> ").Append(status).AppendLine();
        if (note is not null) sb.Append("Info: ").AppendLine(note);
        sb.AppendLine("--- Request headers");
        foreach (var (k, v) in reqHeaders) sb.Append(k).Append(": ").AppendLine(v);
        if (reqBody.Length > 0) sb.AppendLine("--- Request body").AppendLine(reqBody);
        sb.AppendLine("--- Response headers");
        foreach (var (k, v) in respHeaders) sb.Append(k).Append(": ").AppendLine(v);
        if (respBody.Length > 0) sb.AppendLine("--- Response body").AppendLine(respBody);
        sb.AppendLine().AppendLine();
        _httpText.WriteRawText(sb.ToString());
    }

    public void LogSession(object entry)
    {
        lock (_lock) _sessions.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>Passive mode: log every raw UDP packet in readable form.</summary>
    public void LogRawUdp(bool clientToServer, IPEndPoint from, IPEndPoint to, string info, ReadOnlySpan<byte> data)
    {
        _rawText ??= new TextPacketLog(Path.Combine(Directory, "udp-raw.log"), _opts.TextLogMaxBytes);
        _rawText.Write(clientToServer ? "Client->Server" : "Server->Client", from, to, null, null, info, data);
    }

    /// <summary>Logs a decrypted payload (direction, session).</summary>
    public void LogZenFabric(Direction dir, int session, ReadOnlySpan<byte> payload, string? note = null, DateTimeOffset? time = null)
    {
        _zfcap.Write(dir, session, payload, time);
        var ok = ZfOps.TryParse(payload, out var op, out var err);
        var opcode = payload.Length >= 4 ? BitConverter.ToInt32(payload) : -1;
        var entry = new
        {
            t = time ?? DateTimeOffset.UtcNow,
            session,
            dir = dir == Direction.ClientToServer ? "C2S" : "S2C",
            op = ok ? op!.Code.ToString() : "PARSE_ERROR",
            opcode,
            len = payload.Length,
            note,
            error = err,
            data = ok ? OpDescriber.Describe(op!) : null,
            raw = ok ? null : OpDescriber.Hex(payload, 4096),
        };
        lock (_lock) _zf.WriteLine(JsonSerializer.Serialize(entry, Json));

        // Text log + Wireshark
        var c2s = dir == Direction.ClientToServer;
        var ep = _endpoints.TryGetValue(session, out var e) ? e : (Client: Unknown, Server: Unknown);
        var from = c2s ? ep.Client : ep.Server;
        var to = c2s ? ep.Server : ep.Client;
        var title = opcode == -1 ? "ENCRYPTED" : $"Op: {opcode} {(ok ? op!.Code.ToString() : "?")}";
        var info = ok ? (opcode == -1 ? note : OpDescriber.Summary(op!) + (note is null ? "" : "  (" + note + ")")) : $"PARSE ERROR: {err}";
        _zfText.Write(c2s ? "Client->Server" : "Server->Client", from, to, session.ToString(), title, info, payload, time);
        _zfPcap.Write(from, to, payload, time);

        if (!_opts.VerboseConsole || !ok) { if (!ok) Console.WriteLine($"  [{session}] {(c2s ? "C->S" : "S->C")} PARSE ERROR {err}"); return; }
        if (op is PingOp or PongOp || opcode == -1) return;
        if (op is ComponentUpdateOp u && _opts.QuietComponents.Contains(u.ComponentId)) return;
        Console.WriteLine($"  [{session}] {(c2s ? "C->S" : "S->C")} {OpDescriber.Summary(op!)}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _http.Dispose();
            _zf.Dispose();
            _sessions.Dispose();
            _zfcap.Dispose();
            _zfText.Dispose();
            _httpText.Dispose();
            _zfPcap.Dispose();
            _rawText?.Dispose();
        }
    }
}
