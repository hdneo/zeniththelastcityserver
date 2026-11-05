using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZenithServer.Accounts;

public sealed class Account
{
    public string PlayFabId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    /// <summary>Platform ID (e.g. Oculus/Steam user_id) -> for loginrift/checkrift and the Steam variants.</summary>
    public string? PlatformUserId { get; set; }
    public string Platform { get; set; } = "Oculus";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> UserData { get; set; } = new();
    public Dictionary<string, string> ReadOnlyData { get; set; } = new();
    public Dictionary<string, int> Statistics { get; set; } = new();
}

public sealed class Session
{
    public required string PlayFabId { get; init; }
    public required string SessionTicket { get; init; }
    public required string AuthToken { get; init; }
    public required string SessionId { get; init; }
    public string? DeploymentId { get; set; }
    public string? JoinToken { get; set; }
    public string? IdentityToken { get; set; }
    public DateTime Created { get; } = DateTime.UtcNow;
}

/// <summary>Thread-safe, JSON-persisted account database (data/accounts.json).</summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Account> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Session> _sessionsByTicket = new();
    private readonly ConcurrentDictionary<string, Session> _sessionsByToken = new();
    private readonly ConcurrentDictionary<string, Session> _sessionsByIdentity = new();
    private readonly ILogger<AccountStore> _log;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AccountStore(ServerOptions opts, ILogger<AccountStore> log)
    {
        _log = log;
        _path = Path.Combine(opts.DataDirectory, "accounts.json");
        if (File.Exists(_path))
        {
            var list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_path)) ?? [];
            foreach (var a in list) _byId[a.PlayFabId] = a;
            _log.LogInformation("{Count} accounts loaded", _byId.Count);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byId.Values.ToList(), Json));
            File.Move(tmp, _path, true);
        }
    }

    public static string NewPlayFabId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    public static string NewToken(int bytes = 24) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var p = stored.Split(':');
        if (p.Length != 2) return false;
        var salt = Convert.FromBase64String(p[0]);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(p[1]));
    }

    public Account? FindById(string playFabId)
    {
        lock (_lock) return _byId.GetValueOrDefault(playFabId);
    }

    public Account? FindByPlatformUser(string platformUserId)
    {
        lock (_lock) return _byId.Values.FirstOrDefault(a => a.PlatformUserId == platformUserId);
    }

    public Account? FindByUsername(string username)
    {
        lock (_lock) return _byId.Values.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    public Account Create(string username, string? platformUserId, string? email = null, string? password = null)
    {
        var a = new Account
        {
            PlayFabId = NewPlayFabId(),
            Username = string.IsNullOrWhiteSpace(username) ? "Player" + Random.Shared.Next(1000, 9999) : username,
            PlatformUserId = platformUserId,
            Email = email,
            PasswordHash = string.IsNullOrEmpty(password) ? null : HashPassword(password),
        };
        lock (_lock) _byId[a.PlayFabId] = a;
        Save();
        _log.LogInformation("Account created: {Name} ({Id})", a.Username, a.PlayFabId);
        return a;
    }

    public void Update(Account a, Action<Account> change)
    {
        lock (_lock) change(a);
        Save();
    }

    public Session CreateSession(Account a)
    {
        var s = new Session
        {
            PlayFabId = a.PlayFabId,
            SessionTicket = $"{a.PlayFabId}-{NewToken(12)}",
            AuthToken = NewToken(),
            SessionId = Guid.NewGuid().ToString(),
        };
        _sessionsByTicket[s.SessionTicket] = s;
        _sessionsByToken[s.AuthToken] = s;
        Update(a, x => x.LastLogin = DateTime.UtcNow);
        return s;
    }

    public Session? BySessionTicket(string? ticket) => ticket is null ? null : _sessionsByTicket.GetValueOrDefault(ticket);
    public Session? ByAuthToken(string? token) => token is null ? null : _sessionsByToken.GetValueOrDefault(token);
    public Session? ByIdentityToken(string? token) => token is null ? null : _sessionsByIdentity.GetValueOrDefault(token);

    /// <summary>Issues join/identity tokens for a realm connection.</summary>
    public void IssueJoinTokens(Session s, string deploymentId)
    {
        s.DeploymentId = deploymentId;
        s.JoinToken = NewToken();
        s.IdentityToken = NewToken(32);
        _sessionsByIdentity[s.IdentityToken] = s;
    }

    /// <summary>Finds a session by bearer token, session ticket or PlayFab ID.</summary>
    public Session? Resolve(string? bearerOrTicket, string? playFabId = null)
    {
        if (!string.IsNullOrEmpty(bearerOrTicket))
        {
            var t = bearerOrTicket.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearerOrTicket[7..].Trim() : bearerOrTicket.Trim();
            var s = ByAuthToken(t) ?? BySessionTicket(t);
            if (s is not null) return s;
        }
        if (!string.IsNullOrEmpty(playFabId))
            return _sessionsByTicket.Values.Where(x => x.PlayFabId == playFabId).OrderByDescending(x => x.Created).FirstOrDefault();
        return null;
    }
}
