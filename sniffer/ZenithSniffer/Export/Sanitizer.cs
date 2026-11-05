using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZenithSniffer.Export;

/// <summary>
/// Replaces personal/secret values with PSEUDONYMS OF THE SAME BYTE LENGTH.
///  * same length   -> binary data (length-prefixed strings in SchemaObject) stays valid
///  * same shape    -> letters stay letters, digits stay digits, '@' '.' '-' are kept
///  * consistent    -> the same player gets the same pseudonym throughout the export
///  * irreversible  -> derived via HMAC with a random key that only exists in memory
/// </summary>
public sealed partial class Sanitizer
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, (string Replacement, string Category)> _map = new(StringComparer.Ordinal);
    private List<(byte[] From, byte[] To)>? _byteRules;

    public IReadOnlyDictionary<string, (string Replacement, string Category)> Mapping => _map;

    /// <summary>JSON keys whose values are replaced completely (case-insensitive).</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sessionTicket", "authToken", "token", "code", "joinToken", "identityToken", "spatialLoginToken",
        "spatialPlayerIdentityToken", "entityToken", "sessionID", "sessionId", "password", "ticket", "steamTicket",
        "refreshToken", "accessToken", "user_id", "userId", "steamId", "oculusId", "customId", "email",
        "playFabId", "playerId", "username", "displayName", "titleDisplayName", "zen_id", "referralCode",
        "referrer", "accountLogin", "storeAccountName", "vivoxAccountName", "psnAccountId", "characterName",
        "anonymousCharacterId", "anonymousPlayerId", "linkedToPlatformID", "symmetricKeyHex",
    };

    /// <summary>Numeric values that are IDs too (other numbers such as "code":200 stay unchanged).</summary>
    private static readonly HashSet<string> NumericSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_id", "userId", "steamId", "oculusId", "psnAccountId",
    };

    /// <summary>HTTP headers whose values are replaced completely.</summary>
    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Authentication", "X-Authorization", "X-EntityToken", "X-SecretKey", "Cookie", "Set-Cookie",
    };

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    // PlayFab session ticket: <ID>-<HEX>-<HEX>-<TitleId>-<HEX>-<base64>
    [GeneratedRegex(@"[0-9A-F]{14,16}-[0-9A-F]{8,}-[0-9A-F]{8,}-[0-9A-Za-z]{4,6}-[0-9A-F]{8,}-[A-Za-z0-9+/=]{8,}")]
    private static partial Regex SessionTicket();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9.\-]{1,190}\.[A-Za-z]{2,24}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![0-9])7656119[0-9]{10}(?![0-9])")]
    private static partial Regex SteamId64();

    /// <summary>PlayFab IDs are 14–16 upper-case hex characters.</summary>
    [GeneratedRegex(@"^[0-9A-F]{14,16}$")]
    public static partial Regex PlayFabIdExact();

    /// <summary>Is this value secret under this JSON key? ("code" only if long enough to be a login ticket.)</summary>
    private static bool IsSecret(string key, string value) =>
        SecretKeys.Contains(key) && !(key.Equals("code", StringComparison.OrdinalIgnoreCase) && value.Length < 32);

    // ------------------------------------------------------------------ Registration

    /// <summary>Registers a value for replacement (4+ bytes; empty/short values are ignored).</summary>
    public void Add(string? original, string category)
    {
        if (string.IsNullOrEmpty(original) || Encoding.UTF8.GetByteCount(original) < 4 || _map.ContainsKey(original)) return;
        _map[original] = (Scramble(original), category);
        _byteRules = null;
    }

    /// <summary>Finds known patterns (JWTs, tickets, e-mails, Steam IDs) in a text and registers them.</summary>
    public void Discover(string text)
    {
        foreach (Match m in Jwt().Matches(text)) Add(m.Value, "JWT token");
        foreach (Match m in SessionTicket().Matches(text)) Add(m.Value, "session ticket");
        foreach (Match m in Email().Matches(text)) Add(m.Value, "e-mail");
        foreach (Match m in SteamId64().Matches(text)) Add(m.Value, "Steam ID");
    }

    /// <summary>Shape-preserving, irreversible replacement with identical UTF-8 byte length.</summary>
    public string Scramble(string original)
    {
        var src = Encoding.UTF8.GetBytes(original);
        var stream = new List<byte>();
        for (var block = 0; stream.Count < src.Length; block++)
            stream.AddRange(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(original + "\u0001" + block)));
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];
            var h = stream[i];
            dst[i] = b switch
            {
                >= (byte)'A' and <= (byte)'Z' => (byte)('A' + h % 26),
                >= (byte)'a' and <= (byte)'z' => (byte)('a' + h % 26),
                >= (byte)'0' and <= (byte)'9' => (byte)('0' + h % 10),
                >= 0x80 => (byte)'x',
                _ => b,
            };
        }
        // Hex values stay hex (PlayFab IDs, keys) – otherwise they would no longer look like IDs.
        if (Regex.IsMatch(original, "^[0-9A-Fa-f]+$"))
            for (var i = 0; i < dst.Length; i++)
            {
                var upper = src[i] is >= (byte)'A' and <= (byte)'F';
                var hex = "0123456789abcdef"[stream[i] % 16];
                dst[i] = (byte)(upper ? char.ToUpperInvariant(hex) : hex);
            }
        return Encoding.ASCII.GetString(dst);
    }

    // ------------------------------------------------------------------ Apply: text

    public string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        Discover(text);
        foreach (var (orig, (repl, _)) in _map.OrderByDescending(kv => kv.Key.Length))
        {
            if (orig.Length >= 6) text = text.Replace(orig, repl, StringComparison.Ordinal);
            else text = text.Replace("\"" + orig + "\"", "\"" + repl + "\"", StringComparison.Ordinal);
        }
        return text;
    }

    public string SanitizeHeader(string name, string value)
    {
        if (!SecretHeaders.Contains(name)) return SanitizeText(value);
        var prefix = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[..7] : "";
        var secret = value[prefix.Length..];
        Add(secret, "HTTP header " + name);
        return prefix + (_map.TryGetValue(secret, out var r) ? r.Replacement : Scramble(secret));
    }

    /// <summary>Collects values of secret JSON keys and PlayFab-ID-shaped strings (pass 1).</summary>
    public void CollectFromJson(JsonNode? node, string? key = null)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    // Key names can be IDs too, e.g. {"<characterId>": {...}}
                    if (PlayFabIdExact().IsMatch(k)) Add(k, "PlayFab ID (JSON key)");
                    Discover(k);
                    CollectFromJson(v, k);
                }
                break;
            case JsonArray a:
                foreach (var v in a) CollectFromJson(v, key);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (key is not null && IsSecret(key, s)) Add(s, "JSON: " + key);
                else if (PlayFabIdExact().IsMatch(s)) Add(s, "PlayFab ID");
                Discover(s);
                break;
            case JsonValue v when key is not null && NumericSecretKeys.Contains(key):
                Add(v.ToJsonString().Trim('"'), "JSON: " + key);
                break;
        }
    }

    /// <summary>Replaces all string values of a JSON tree (pass 2). Returns the new node.</summary>
    public JsonNode? SanitizeJson(JsonNode? node, string? key = null)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    var newKey = SanitizeText(k);
                    copy[newKey] = SanitizeJson(v, k);
                }
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (var v in a) copy.Add(SanitizeJson(v, key));
                return copy;
            }
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (key is not null && IsSecret(key, s))
                {
                    Add(s, "JSON: " + key);
                    return JsonValue.Create(_map.TryGetValue(s, out var r) ? r.Replacement : s);
                }
                return JsonValue.Create(SanitizeText(s));
            case JsonValue v when key is not null && NumericSecretKeys.Contains(key):
                return JsonValue.Create(Scramble(v.ToJsonString().Trim('"')));
            default:
                return node?.DeepClone();
        }
    }

    // ------------------------------------------------------------------ Apply: binary

    /// <summary>
    /// Replaces all known values in binary data (same length, structure stays valid).
    /// Values under 6 bytes only where they occur exactly as a length-prefixed string.
    /// </summary>
    public int SanitizeBytes(byte[] data)
    {
        Discover(Encoding.Latin1.GetString(data));
        _byteRules ??= _map
            .Select(kv => (From: Encoding.UTF8.GetBytes(kv.Key), To: Encoding.UTF8.GetBytes(kv.Value.Replacement)))
            .Where(r => r.From.Length == r.To.Length)
            .OrderByDescending(r => r.From.Length)
            .ToList();
        var count = 0;
        foreach (var (from, to) in _byteRules)
        {
            var span = data.AsSpan();
            var start = 0;
            while (start <= data.Length - from.Length)
            {
                var idx = span[start..].IndexOf(from);
                if (idx < 0) break;
                var pos = start + idx;
                var exactString = pos >= 4 && BinaryPrimitives.ReadInt32LittleEndian(span[(pos - 4)..]) == from.Length;
                if (from.Length >= 6 || exactString)
                {
                    to.CopyTo(span[pos..]);
                    count++;
                }
                start = pos + from.Length;
            }
        }
        return count;
    }

    /// <summary>Finds length-prefixed UTF-8 strings ([int32 len][bytes]) in SchemaObject data.</summary>
    public static IEnumerable<string> ExtractSchemaStrings(byte[] data, int minLen = 3, int maxLen = 256)
    {
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i));
            if (len < minLen || len > maxLen || i + 4 + len > data.Length) continue;
            var bytes = data.AsSpan(i + 4, len);
            if (!IsPrintableUtf8(bytes)) continue;
            yield return Encoding.UTF8.GetString(bytes);
        }
    }

    private static bool IsPrintableUtf8(ReadOnlySpan<byte> b)
    {
        foreach (var x in b) if (x < 0x20 || x == 0x7F) return false;
        try { _ = new UTF8Encoding(false, true).GetString(b); return true; }
        catch { return false; }
    }
}
