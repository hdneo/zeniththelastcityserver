using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zenith.Protocol.ZenFabric;
using ZenithSniffer.Capture;

namespace ZenithSniffer.Export;

/// <summary>
/// Creates a sanitized, shareable copy (+ ZIP) of a capture folder.
///
/// Left out:     sessions.jsonl (tokens, session keys), passive pcap/udp-raw.log (own IP addresses),
///               encrypted passive records (unreadable anyway).
/// Replaced:     tokens, tickets, auth headers, e-mail, user names, Steam/Oculus/PlayFab IDs,
///               character names and account fields from the identity components – with pseudonyms
///               of the same length, so the game data stays structurally unchanged.
/// Regenerated:  zenfabric.zfcap/.jsonl/.log/-decrypted.pcap and http.jsonl/.log from the sanitized data.
/// </summary>
public static class CaptureExporter
{
    public const string MappingFileName = "_export-mapping-DO-NOT-SHARE.txt";
    public const string ReportFileName = "README-EXPORT.txt";

    /// <summary>Components with account/personal data: every string they contain is pseudonymized.</summary>
    private static readonly HashSet<uint> IdentityComponents =
    [
        60,    // Improbable.Restricted.Worker            (worker ID contains the PlayFab ID)
        61,    // Improbable.Restricted.PlayerClient      (PlayerIdentity)
        1000,  // Zenith.PlayerAuthComponent              (PlayfabId, SessionTicket)
        1008,  // Zenith.CharacterCredentialsComponent    (name, PlayfabId, Vivox, store account, login, referral ...)
        13003, // Improbable.Gdk.PlayerLifecycle.OwningWorker
    ];

    /// <summary>Strings that occur in identity components but are not personal data.</summary>
    private static bool IsHarmless(string s) =>
        Regex.IsMatch(s, "^[a-z0-9_]+$") || s is "UnityClient" or "UnityGameLogic" or "Player" or "Worker";

    public static int Run(string sourceDir, SnifferOptions baseOpts, bool zip = true)
    {
        sourceDir = Path.GetFullPath(sourceDir.TrimEnd('\\', '/'));
        if (!Directory.Exists(sourceDir)) { Console.WriteLine($"Folder not found: {sourceDir}"); return 1; }

        var dst = sourceDir + "-export";
        for (var i = 2; Directory.Exists(dst) || File.Exists(dst + ".zip"); i++) dst = $"{sourceDir}-export{i}";
        Console.WriteLine($"Source : {sourceDir}");
        Console.WriteLine($"Export : {dst}");

        var san = new Sanitizer();
        var stats = new Dictionary<string, int>();
        void Count(string k, int n = 1) => stats[k] = stats.GetValueOrDefault(k) + n;

        // ---------------------------------------------------------------- read + pass 1 (collect)
        var endpoints = new Dictionary<int, (IPEndPoint Client, IPEndPoint Server)>();
        var sessionsFile = Path.Combine(sourceDir, "sessions.jsonl");
        if (File.Exists(sessionsFile))
        {
            foreach (var line in ReadLines(sessionsFile))
            {
                var n = Parse(line) as JsonObject;
                if (n is null) continue;
                foreach (var key in new[] { "joinToken", "identityToken", "symmetricKeyHex" })
                    san.Add(n[key]?.GetValue<string>(), "sessions: " + key);
                var session = n["session"]?.GetValue<int>() ?? n["flow"]?.GetValue<int>() ?? 0;
                var client = n["client"]?.GetValue<string>();
                var server = n["upstream"]?.GetValue<string>() ?? n["server"]?.GetValue<string>();
                if (session > 0 && client is not null && server is not null &&
                    IPEndPoint.TryParse(client, out var c) && IPEndPoint.TryParse(server, out var s))
                {
                    endpoints[session] = (new IPEndPoint(IPAddress.Parse("10.0.0.2"), c.Port), s);
                    if (!IPAddress.IsLoopback(c.Address)) san.Add(c.Address.ToString(), "client IP");
                }
            }
        }

        var httpEntries = new List<JsonObject>();
        var httpFile = Path.Combine(sourceDir, "http.jsonl");
        if (File.Exists(httpFile))
            foreach (var line in ReadLines(httpFile))
                if (Parse(line) is JsonObject o) httpEntries.Add(o);
        foreach (var e in httpEntries) Walk(e, san, collect: true);

        var records = new List<ZfCaptureRecord>();
        var zfcapFile = Path.Combine(sourceDir, "zenfabric.zfcap");
        if (File.Exists(zfcapFile))
            foreach (var r in ZfCaptureReader.Read(zfcapFile))
            {
                if (r.Payload.Length >= 4 && BitConverter.ToInt32(r.Payload) == -1) { Count("encrypted passive records left out"); continue; }
                records.Add(r);
            }
        foreach (var r in records) CollectFromPayload(r.Payload, san);

        Console.WriteLine($"Found: {httpEntries.Count} HTTP entries, {records.Count} ZenFabric messages, {san.Mapping.Count} values to replace");

        // ---------------------------------------------------------------- pass 2 (write)
        var opts = new SnifferOptions { VerboseConsole = false, TextLogMaxBytes = baseOpts.TextLogMaxBytes, QuietComponents = baseOpts.QuietComponents };
        using (var cap = new CaptureSession(opts, explicitDirectory: dst))
        {
            foreach (var (id, ep) in endpoints) cap.RegisterEndpoints(id, ep.Client, ep.Server);

            foreach (var r in records)
            {
                var payload = (byte[])r.Payload.Clone();
                if (ZfOps.TryParse(payload, out var op, out _) && op is AuthWorkerOp auth)
                {
                    // Zero the key parts (same length); the token is replaced below.
                    auth.SymmetricKeyParts = auth.SymmetricKeyParts.Select(p => new byte[p.Length]).ToList();
                    payload = auth.Serialize();
                    Count("AuthWorker: key parts zeroed");
                }
                Count("replacements in ZenFabric data", san.SanitizeBytes(payload));
                cap.LogZenFabric(r.Direction, r.SessionId, payload, null, r.Time);
            }

            foreach (var e in httpEntries)
            {
                Walk(e, san, collect: false);
                cap.LogHttp(e);
                cap.LogHttpText(
                    Str(e["method"]) ?? Str(e["kind"]) ?? "", Str(e["url"]) ?? "",
                    Headers(e["requestHeaders"]), BodyText(e["requestBody"] ?? e["body"]),
                    e["status"]?.GetValue<int>() ?? 0,
                    Headers(e["responseHeaders"]), BodyText(e["responseBody"]),
                    Str(e["note"]), e["t"] is { } t ? DateTimeOffset.Parse(t.GetValue<string>()) : null);
            }
        }

        // CaptureSession always creates an (empty) sessions.jsonl – not wanted in the export.
        var emptySessions = Path.Combine(dst, "sessions.jsonl");
        if (File.Exists(emptySessions) && new FileInfo(emptySessions).Length == 0) File.Delete(emptySessions);

        // ---------------------------------------------------------------- self-check
        var leftovers = SelfCheck(dst, san);

        foreach (var g in san.Mapping.Values.GroupBy(v => v.Category)) Count("replaced: " + g.Key, g.Count());
        WriteReport(dst, stats, leftovers, httpEntries.Count, records.Count);
        WritePrivateMapping(sourceDir, san);

        string? zipPath = null;
        if (zip)
        {
            zipPath = dst + ".zip";
            ZipFile.CreateFromDirectory(dst, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }

        Console.WriteLine();
        foreach (var (k, v) in stats.OrderBy(k => k.Key)) Console.WriteLine($"  {k,-55} {v}");
        Console.WriteLine();
        if (leftovers.Count > 0)
        {
            Console.WriteLine($"WARNING: {leftovers.Count} original value(s) still found in the export – DO NOT SHARE, see {ReportFileName}");
            return 2;
        }
        Console.WriteLine("Self-check: no original values left in the export.");
        Console.WriteLine(zipPath is null ? $"Done: {dst}" : $"Done: {zipPath}");
        Console.WriteLine($"Mapping (local only, DO NOT SHARE): {Path.Combine(sourceDir, MappingFileName)}");
        return 0;
    }

    // ==================================================================== helpers

    private static void CollectFromPayload(byte[] payload, Sanitizer san)
    {
        if (!ZfOps.TryParse(payload, out var op, out _)) { san.Discover(Encoding.Latin1.GetString(payload)); return; }

        if (op is AuthWorkerOp a) san.Add(a.IdentityToken, "ZenFabric: identity token");

        IEnumerable<ComponentData> comps = op switch
        {
            AddComponentOp x => [x.Data],
            ComponentUpdateOp x => [new ComponentData(x.ComponentId, x.Data)],
            ComponentSetAuthorityChangeOp x => x.CanonicalData,
            CreateEntityRequestOp x => x.Components,
            EntityQueryResponseOp x => x.Results.SelectMany(e => e.Components),
            _ => [],
        };
        foreach (var c in comps.Where(c => IdentityComponents.Contains(c.ComponentId)))
            foreach (var s in Sanitizer.ExtractSchemaStrings(c.Data))
                if (!IsHarmless(s)) san.Add(s, $"component {c.ComponentId}");

        // PlayFab IDs everywhere (also of other players: groups, trading, chat senders ...)
        foreach (var s in Sanitizer.ExtractSchemaStrings(payload, 14, 16))
            if (Sanitizer.PlayFabIdExact().IsMatch(s)) san.Add(s, "PlayFab ID");
        san.Discover(Encoding.Latin1.GetString(payload));
    }

    /// <summary>Walks an HTTP entry: collect (pass 1) or replace (pass 2).</summary>
    private static void Walk(JsonObject e, Sanitizer san, bool collect)
    {
        foreach (var hk in new[] { "requestHeaders", "responseHeaders" })
            if (e[hk] is JsonObject h)
                foreach (var name in h.Select(kv => kv.Key).ToList())
                {
                    var v = Str(h[name]) ?? "";
                    var sv = san.SanitizeHeader(name, v);
                    if (!collect) h[name] = sv;
                }

        foreach (var key in e.Select(kv => kv.Key).ToList())
        {
            if (key is "requestHeaders" or "responseHeaders") continue;
            var node = e[key];
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
            {
                var trimmed = s.TrimStart();
                var json = trimmed.StartsWith('{') || trimmed.StartsWith('[') ? Parse(s) : null;
                if (json is not null)
                {
                    if (collect) san.CollectFromJson(json);
                    else e[key] = san.SanitizeJson(json)!.ToJsonString();
                }
                else if (collect) san.Discover(s);
                else e[key] = san.SanitizeText(s);
            }
            else if (node is JsonObject body && body["base64"] is not null)
            {
                if (!collect) e[key] = "<binary, removed by export>";
            }
        }
    }

    private static List<string> SelfCheck(string dir, Sanitizer san)
    {
        var originals = san.Mapping.Keys.Where(k => Encoding.UTF8.GetByteCount(k) >= 6).ToList();
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var bytes = File.ReadAllBytes(file);
            var latin = Encoding.Latin1.GetString(bytes);
            foreach (var o in originals)
            {
                var asLatin = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(o));
                if (latin.Contains(asLatin, StringComparison.Ordinal))
                    found.Add($"{Path.GetFileName(file)}: value of category '{san.Mapping[o].Category}' ({o.Length} characters)");
            }
        }
        return found.Distinct().ToList();
    }

    private static void WriteReport(string dir, Dictionary<string, int> stats, List<string> leftovers, int http, int zf)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Zenith capture – sanitized export");
        sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm}   HTTP entries: {http}   ZenFabric messages: {zf}");
        sb.AppendLine();
        sb.AppendLine("Included files:");
        sb.AppendLine("  zenfabric.log               readable (hex/ASCII, MxO style)");
        sb.AppendLine("  zenfabric-decrypted.pcap    for Wireshark");
        sb.AppendLine("  zenfabric.jsonl / .zfcap    machine-readable (ZenithSniffer decode)");
        sb.AppendLine("  http.log / http.jsonl       web requests (login, realms, PlayFab, Nexus)");
        sb.AppendLine();
        sb.AppendLine("What was sanitized:");
        sb.AppendLine("  - sessions.jsonl (tokens, session keys) and passive raw data (IP addresses) are NOT included");
        sb.AppendLine("  - tokens, session tickets, auth headers, e-mail, user names, Steam/Oculus/PlayFab IDs,");
        sb.AppendLine("    character names and account fields were replaced with pseudonyms of the same length");
        sb.AppendLine("    (the same player has the same pseudonym throughout the export; not reversible)");
        sb.AppendLine("  - client address replaced with 10.0.0.2; server addresses (public game servers) are kept");
        sb.AppendLine();
        sb.AppendLine("NOT removed automatically: free text typed by players (e.g. chat messages, guild names, notes).");
        sb.AppendLine("So don't write anything personal in chat while capturing.");
        sb.AppendLine();
        sb.AppendLine("Statistics:");
        foreach (var (k, v) in stats.OrderBy(k => k.Key)) sb.AppendLine($"  {k,-55} {v}");
        sb.AppendLine();
        if (leftovers.Count == 0) sb.AppendLine("Self-check: no original values found in the export.");
        else
        {
            sb.AppendLine("WARNING – the self-check found leftovers (DO NOT SHARE this export):");
            foreach (var l in leftovers) sb.AppendLine("  " + l);
        }
        File.WriteAllText(Path.Combine(dir, ReportFileName), sb.ToString(), new UTF8Encoding(false));
    }

    private static void WritePrivateMapping(string sourceDir, Sanitizer san)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LOCAL ONLY – DO NOT SHARE. Mapping original -> pseudonym of the last export.");
        sb.AppendLine();
        foreach (var (orig, (repl, cat)) in san.Mapping.OrderBy(k => k.Value.Category))
            sb.AppendLine($"[{cat}]\n  {Short(orig)}\n  -> {Short(repl)}");
        File.WriteAllText(Path.Combine(sourceDir, MappingFileName), sb.ToString(), new UTF8Encoding(false));
    }

    private static string Short(string s) => s.Length <= 120 ? s : s[..120] + $"… ({s.Length} characters)";

    private static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs, Encoding.UTF8);
        while (r.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }

    private static JsonNode? Parse(string s)
    {
        try { return JsonNode.Parse(s); } catch { return null; }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString();

    private static Dictionary<string, string> Headers(JsonNode? n) =>
        n is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => Str(kv.Value) ?? "") : new();

    private static string BodyText(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };
}
