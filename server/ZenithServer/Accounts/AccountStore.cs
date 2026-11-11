using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ZenithServer.Storage;

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
    public JsonObject Preferences { get; set; } = new();
    public List<StoredCharacter> Characters { get; set; } = [];
    public List<StoredPlayerReport> PlayerReports { get; set; } = [];
    public int? LocalZennies { get; set; }
    public List<string> CosmeticItems { get; set; } = [];

    internal Account Copy()
    {
        var copy = (Account)MemberwiseClone();
        copy.UserData = new(UserData, UserData.Comparer);
        copy.ReadOnlyData = new(ReadOnlyData, ReadOnlyData.Comparer);
        copy.Statistics = new(Statistics, Statistics.Comparer);
        copy.Preferences = (JsonObject)Preferences.DeepClone();
        copy.Characters = [..Characters];
        copy.PlayerReports = [..PlayerReports];
        copy.CosmeticItems = [..CosmeticItems];
        return copy;
    }

    internal void Restore(Account original)
    {
        PlayFabId = original.PlayFabId;
        Username = original.Username;
        Email = original.Email;
        PasswordHash = original.PasswordHash;
        PlatformUserId = original.PlatformUserId;
        Platform = original.Platform;
        Created = original.Created;
        LastLogin = original.LastLogin;
        UserData = original.UserData;
        ReadOnlyData = original.ReadOnlyData;
        Statistics = original.Statistics;
        Preferences = original.Preferences;
        Characters = original.Characters;
        PlayerReports = original.PlayerReports;
        LocalZennies = original.LocalZennies;
        CosmeticItems = original.CosmeticItems;
    }
}

public sealed record StoredCharacter(string Id, string Name, uint Type, uint Role, uint Gender,
    IReadOnlyDictionary<uint, string> Appearance);

public sealed record StoredPlayerReport(string ReporteeId, string? Comment, DateTimeOffset SubmittedAt);

internal sealed record CosmeticWallet(int Balance, string[] Items);

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

/// <summary>In-memory accounts backed by the configured game storage.</summary>
public sealed class AccountStore
{
    private readonly IGameStorage _storage;
    private readonly object _lock = new();
    private readonly Dictionary<string, Account> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Session> _sessionsByTicket = new();
    private readonly ConcurrentDictionary<string, Session> _sessionsByToken = new();
    private readonly ConcurrentDictionary<string, Session> _sessionsByIdentity = new();
    private readonly ILogger<AccountStore> _log;
    private readonly int _startingZennies;

    public AccountStore(ServerOptions opts, IGameStorage storage, ILogger<AccountStore> log)
    {
        _log = log;
        _storage = storage;
        _startingZennies = Math.Max(0, opts.StartingZennies);
        foreach (var account in _storage.LoadAccounts()) _byId[account.PlayFabId] = account;
        _log.LogInformation("{Count} accounts loaded", _byId.Count);
    }

    private void Save(Account account) => _storage.SaveAccount(account, _byId.Values);

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

    public JsonObject ReadPreferences(Account account)
    {
        lock (_lock) return (JsonObject)account.Preferences.DeepClone();
    }

    internal CosmeticWallet ReadCosmeticWallet(Account account)
    {
        lock (_lock) return new(account.LocalZennies ?? _startingZennies, account.CosmeticItems.ToArray());
    }

    internal (int Before, CosmeticWallet Wallet, bool Succeeded, bool Purchased) BuyCosmetic(Account account,
        string itemId, int price)
    {
        lock (_lock)
        {
            var before = account.LocalZennies ?? _startingZennies;
            if (account.CosmeticItems.Contains(itemId, StringComparer.Ordinal))
                return (before, ReadCosmeticWallet(account), true, false);
            if (before < price) return (before, ReadCosmeticWallet(account), false, false);

            var oldBalance = account.LocalZennies;
            account.LocalZennies = before - price;
            account.CosmeticItems.Add(itemId);
            try { Save(account); }
            catch
            {
                account.LocalZennies = oldBalance;
                account.CosmeticItems.RemoveAt(account.CosmeticItems.Count - 1);
                throw;
            }
            return (before, ReadCosmeticWallet(account), true, true);
        }
    }

    public (bool Updated, int SubmissionsRemaining) ReportPlayer(Account reporter, Account reportee, string? comment)
    {
        const int dailyLimit = 5;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var used = reporter.PlayerReports.Count(report => report.SubmittedAt.UtcDateTime.Date == now.UtcDateTime.Date);
            if (used >= dailyLimit) return (false, 0);

            reporter.PlayerReports.Add(new StoredPlayerReport(reportee.PlayFabId, comment, now));
            try { Save(reporter); }
            catch
            {
                reporter.PlayerReports.RemoveAt(reporter.PlayerReports.Count - 1);
                throw;
            }
            return (true, dailyLimit - used - 1);
        }
    }

    public StoredCharacter[] ListCharacters(Account account)
    {
        lock (_lock) return account.Characters.ToArray();
    }

    public bool IsCharacterNameAvailable(string name)
    {
        lock (_lock)
            return !_byId.Values.SelectMany(a => a.Characters)
                .Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public StoredCharacter? CreateCharacter(Account account, string name, uint type, uint role, uint gender,
        IReadOnlyDictionary<uint, string> appearance)
    {
        lock (_lock)
        {
            if (!IsCharacterNameAvailable(name)) return null;

            var character = new StoredCharacter(NewPlayFabId(), name, type, role, gender, appearance);
            account.Characters.Add(character);
            try { Save(account); }
            catch
            {
                account.Characters.RemoveAt(account.Characters.Count - 1);
                throw;
            }
            return character;
        }
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
        lock (_lock)
        {
            _byId.Add(a.PlayFabId, a);
            try { Save(a); }
            catch
            {
                _byId.Remove(a.PlayFabId);
                throw;
            }
        }
        _log.LogInformation("Account created: {Name} ({Id})", a.Username, a.PlayFabId);
        return a;
    }

    public void Update(Account a, Action<Account> change)
    {
        lock (_lock)
        {
            var original = a.Copy();
            try
            {
                change(a);
                Save(a);
            }
            catch
            {
                a.Restore(original);
                throw;
            }
        }
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
        Update(a, x => x.LastLogin = DateTime.UtcNow);
        _sessionsByTicket[s.SessionTicket] = s;
        _sessionsByToken[s.AuthToken] = s;
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
