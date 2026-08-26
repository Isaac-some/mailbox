using MailArchiver.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.LocalApp;

public class LocalDatabaseSchemaUpgradeTests
{
    [Fact]
    public async Task Upgrade_backfills_explicit_provider_identity_for_existing_accounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE MailAccounts (Id INTEGER PRIMARY KEY, EmailAddress TEXT NOT NULL, Provider TEXT NOT NULL);" +
                "INSERT INTO MailAccounts VALUES (1, 'a@gmail.com', 'IMAP');" +
                "INSERT INTO MailAccounts VALUES (2, 'b@yahoo.co.jp', 'IMAP');" +
                "INSERT INTO MailAccounts VALUES (3, 'c@gmx.de', 'IMAP');" +
                "INSERT INTO MailAccounts VALUES (4, 'alias@example.com', 'MSA');";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);

        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT MailProviderKind FROM MailAccounts ORDER BY Id;";
        await using var reader = await verify.ExecuteReaderAsync();
        var providers = new List<string>();
        while (await reader.ReadAsync())
            providers.Add(reader.GetString(0));

        Assert.Equal(["Gmail", "Yahoo", "Gmx", "Outlook"], providers);
    }

    [Fact]
    public async Task Upgrade_adds_scope_column_to_existing_sqlite_database_without_removing_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE MailAccounts (Id INTEGER PRIMARY KEY, EmailAddress TEXT NOT NULL);" +
                "INSERT INTO MailAccounts (Id, EmailAddress) VALUES (1, 'legacy@outlook.com');";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);

        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);
        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT EmailAddress, OAuthGrantedScopes, OAuthRedirectUri FROM MailAccounts WHERE Id = 1;";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("legacy@outlook.com", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }
}
