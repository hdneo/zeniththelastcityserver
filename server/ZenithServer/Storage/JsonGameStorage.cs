using System.Text.Json;
using ZenithServer.Accounts;

namespace ZenithServer.Storage;

internal sealed class JsonGameStorage(string dataDirectory) : IGameStorage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _accountPath = Path.Combine(dataDirectory, "accounts.json");
    private readonly string _worldPath = Path.Combine(dataDirectory, "world.json");

    public IReadOnlyList<Account> LoadAccounts() => File.Exists(_accountPath)
        ? JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_accountPath)) ?? []
        : [];

    public void SaveAccount(Account account, IReadOnlyCollection<Account> accounts) =>
        WriteFile(_accountPath, JsonSerializer.Serialize(accounts, Json));

    public string? LoadWorld() => File.Exists(_worldPath) ? File.ReadAllText(_worldPath) : null;

    public void SaveWorld(string snapshot) => WriteFile(_worldPath, snapshot);

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
