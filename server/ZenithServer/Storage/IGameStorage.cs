using ZenithServer.Accounts;

namespace ZenithServer.Storage;

public interface IGameStorage
{
    IReadOnlyList<Account> LoadAccounts();
    void SaveAccount(Account account, IReadOnlyCollection<Account> accounts);
    string? LoadWorld();
    void SaveWorld(string snapshot);
}
