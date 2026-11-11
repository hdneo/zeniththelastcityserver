using System.Text.Json;
using MySqlConnector;
using ZenithServer.Accounts;
using ZenithServer.ZenFabric;

namespace ZenithServer.Storage;

internal sealed class MySqlGameStorage : IGameStorage
{
    private readonly string _connectionString;

    public MySqlGameStorage(string connectionString, JsonGameStorage legacy, ILogger<MySqlGameStorage> log)
    {
        var options = new MySqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(options.Database))
            throw new InvalidOperationException("ConnectionStrings:ZenithMySql must specify a database");
        _connectionString = options.ConnectionString;

        using var connection = Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS zenith_accounts (
                playfab_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL PRIMARY KEY,
                payload JSON NOT NULL
            ) ENGINE=InnoDB;
            """;
        schema.ExecuteNonQuery();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS zenith_world (
                id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
                snapshot JSON NULL,
                legacy_imported BOOLEAN NOT NULL DEFAULT FALSE
            ) ENGINE=InnoDB;
            """;
        schema.ExecuteNonQuery();
        schema.CommandText = "INSERT INTO zenith_world (id) VALUES (1) ON DUPLICATE KEY UPDATE id = id";
        schema.ExecuteNonQuery();
        ImportLegacy(connection, legacy, log);
    }

    private void ImportLegacy(MySqlConnection connection, JsonGameStorage legacy, ILogger<MySqlGameStorage> log)
    {
        using var transaction = connection.BeginTransaction();
        using var marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = "SELECT legacy_imported FROM zenith_world WHERE id = 1 FOR UPDATE";
        if (Convert.ToBoolean(marker.ExecuteScalar()))
        {
            transaction.Commit();
            return;
        }

        var accounts = legacy.LoadAccounts();
        var world = legacy.LoadWorld();
        if (world is not null) new EntityStore().RestoreSnapshot(world);

        foreach (var account in accounts) WriteAccount(connection, account, transaction);

        marker.CommandText = """
            UPDATE zenith_world
            SET snapshot = COALESCE(snapshot, @snapshot), legacy_imported = TRUE
            WHERE id = 1
            """;
        marker.Parameters.AddWithValue("@snapshot", world);
        marker.ExecuteNonQuery();
        transaction.Commit();
        log.LogInformation("MySQL storage initialized from local snapshots ({AccountCount} account records, world file present: {HasWorld})",
            accounts.Count, world is not null);
    }

    public IReadOnlyList<Account> LoadAccounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM zenith_accounts ORDER BY playfab_id";
        using var reader = command.ExecuteReader();
        var accounts = new List<Account>();
        while (reader.Read())
            accounts.Add(JsonSerializer.Deserialize<Account>(reader.GetString(0)) ??
                throw new InvalidDataException("MySQL contains an empty account record"));
        return accounts;
    }

    public void SaveAccount(Account account, IReadOnlyCollection<Account> accounts)
    {
        try
        {
            using var connection = Open();
            WriteAccount(connection, account);
        }
        catch (MySqlException ex)
        {
            throw new IOException("Could not save account to MySQL", ex);
        }
    }

    private static void WriteAccount(MySqlConnection connection, Account account, MySqlTransaction? import = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = import;
        command.CommandText = """
            INSERT INTO zenith_accounts (playfab_id, payload) VALUES (@id, @payload)
            ON DUPLICATE KEY UPDATE payload =
            """ + (import is null ? " @payload" : " zenith_accounts.payload");
        command.Parameters.AddWithValue("@id", account.PlayFabId);
        command.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(account));
        command.ExecuteNonQuery();
    }

    public string? LoadWorld()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM zenith_world WHERE id = 1";
        return command.ExecuteScalar() as string;
    }

    public void SaveWorld(string snapshot)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE zenith_world SET snapshot = @snapshot WHERE id = 1";
        command.Parameters.AddWithValue("@snapshot", snapshot);
        command.ExecuteNonQuery();
    }

    private MySqlConnection Open()
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
