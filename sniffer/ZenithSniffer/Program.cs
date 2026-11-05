using System.Net;
using System.Text.Json;
using Zenith.Protocol.Net;
using Zenith.Protocol.ZenFabric;
using ZenithSniffer;
using ZenithSniffer.Capture;
using ZenithSniffer.Export;
using ZenithSniffer.Http;
using ZenithSniffer.Net;
using ZenithSniffer.Passive;
using ZenithSniffer.Udp;

// =====================================================================================
//  ZenithSniffer – records the network traffic of "Zenith: The Last City".
//
//   proxy    Man-in-the-middle for HTTPS (login/realm/PlayFab) + ZenFabric (UDP).
//            Captures EVERYTHING in plain text. Requires hosts file + root certificate (see docs/05).
//   passive  Listen only (raw socket, admin). pcap + handshake; game content stays encrypted.
//   decode   Print a .zfcap file in readable form.
//   certs    Create the local CA and show its path + import command.
//   export   Sanitized, shareable copy of a capture (tokens/IDs/names pseudonymized) + ZIP.
// =====================================================================================

var cfg = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("sniffersettings.json", optional: true)
    .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "sniffersettings.json"), optional: true)
    .AddEnvironmentVariables("ZSNIFF_")
    .AddCommandLine(args.Skip(1).Where(a => a.Contains('=')).ToArray())
    .Build();
var opts = cfg.Get<SnifferOptions>() ?? new SnifferOptions();

var cmd = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
switch (cmd)
{
    case "proxy": await RunProxy(); break;
    case "passive":
        if (OperatingSystem.IsWindows()) RunPassive(); else Console.WriteLine("passive: Windows only");
        break;
    case "decode": Decode(args.ElementAtOrDefault(1)); break;
    case "certs": Certs(); break;
    case "export":
        if (args.ElementAtOrDefault(1) is not { } exportDir || exportDir.Contains('='))
            Console.WriteLine(@"Specify a folder: export <captures\YYYYMMDD-HHMMSS-proxy> [--nozip]");
        else
            Environment.ExitCode = CaptureExporter.Run(exportDir, opts, zip: !args.Contains("--nozip"));
        break;
    default: Help(); break;
}

void Help()
{
    Console.WriteLine("""
        ZenithSniffer – traffic capture for Zenith: The Last City

          ZenithSniffer proxy   [Option=Value ...]                 MITM proxy (HTTPS + UDP, plain text)
          ZenithSniffer passive [--ip IP] [--port N] [--acks]      Passive capture (admin)
          ZenithSniffer decode  <file.zfcap> [--json]              Print a capture in readable form
          ZenithSniffer certs                                      Create / show the local CA
          ZenithSniffer export  <capture-folder> [--nozip]         Sanitized copy for sharing (+ ZIP)

        Options (also in sniffersettings.json): BindAddress, HttpPort, HttpsPort, AdvertiseHost,
        UdpPortBase, OutputDirectory, CertificateDirectory, RewriteRealmList, VerboseConsole, TextLogMaxBytes
        Example: ZenithSniffer proxy HttpsPort=443 AdvertiseHost=127.0.0.1

        Guide: docs/05_Sniffer.md
        """);
}

LocalCertificateAuthority LoadCa() => LocalCertificateAuthority.LoadOrCreate(Path.GetFullPath(opts.CertificateDirectory));

void Certs()
{
    var ca = LoadCa();
    Console.WriteLine($"Root certificate: {ca.RootCerPath}");
    Console.WriteLine($"Thumbprint      : {ca.Root.Thumbprint}");
    Console.WriteLine();
    Console.WriteLine("Import for the current Windows user (run this yourself):");
    Console.WriteLine($"  certutil -user -addstore Root \"{ca.RootCerPath}\"");
    Console.WriteLine("Remove later:");
    Console.WriteLine($"  certutil -user -delstore Root {ca.Root.Thumbprint}");
}

async Task RunProxy()
{
    var ca = LoadCa();
    using var capture = new CaptureSession(opts, "proxy");
    var dns = new UpstreamDns(opts.UpstreamDns, opts.HostOverrides);
    using var udp = new UdpBridgeManager(opts, capture, dns);
    var app = HttpMitmProxy.Build(opts, capture, dns, udp, ca);

    Console.WriteLine("ZenithSniffer – proxy mode");
    Console.WriteLine($"  HTTP {opts.BindAddress}:{opts.HttpPort}, HTTPS {opts.BindAddress}:{opts.HttpsPort}, UDP bridges from {opts.UdpPortBase}");
    Console.WriteLine($"  Capture folder: {capture.Directory}");
    Console.WriteLine($"  Root CA:        {ca.RootCerPath}  (must be imported, see 'certs')");
    Console.WriteLine("  The hosts file must point the game domains to this machine (docs/05_Sniffer.md).");
    Console.WriteLine("  Press Ctrl+C to stop.\n");

    // Check that the real servers are reachable (DNS bypasses the hosts file).
    foreach (var h in new[] { "nexus.zenithmmo.com", "skywave.zenithmmo.com", "4bb7b.playfabapi.com" })
    {
        try { Console.WriteLine($"  Upstream {h} = {await dns.ResolveAsync(h)}"); }
        catch (Exception ex) { Console.WriteLine($"  Upstream {h}: {ex.Message}"); }
    }
    Console.WriteLine();
    await app.RunAsync();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
void RunPassive()
{
    IPAddress? ip = null;
    var ports = new HashSet<int>();
    var logAcks = false;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--ip") ip = IPAddress.Parse(args[++i]);
        else if (args[i] == "--port") ports.Add(int.Parse(args[++i]));
        else if (args[i] == "--acks") logAcks = true;
    }
    ip ??= PassiveCapture.DefaultLocalIp();
    using var capture = new CaptureSession(opts, "passive");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        new PassiveCapture(ip, ports, capture, logAcks).Run(cts.Token);
    }
    catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
    {
        Console.WriteLine("Access denied: passive mode needs administrator rights (raw socket).");
    }
}

void Decode(string? path)
{
    if (path is null || !File.Exists(path)) { Console.WriteLine("Specify a file: decode <file.zfcap>"); return; }
    var json = args.Contains("--json");
    foreach (var r in ZfCaptureReader.Read(path))
    {
        var arrow = r.Direction == Direction.ClientToServer ? "C->S" : "S->C";
        if (!ZfOps.TryParse(r.Payload, out var op, out var err))
        {
            Console.WriteLine($"{r.Time:HH:mm:ss.fff} [{r.SessionId}] {arrow} PARSE ERROR {err} {OpDescriber.Hex(r.Payload, 64)}");
            continue;
        }
        Console.WriteLine($"{r.Time:HH:mm:ss.fff} [{r.SessionId}] {arrow} {OpDescriber.Summary(op!)}");
        if (json) Console.WriteLine("    " + JsonSerializer.Serialize(OpDescriber.Describe(op!)));
    }
}
