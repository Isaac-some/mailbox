using MailArchiver.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.LocalApp;

public class LocalDatabaseSchemaUpgradeTests
{
    [Fact]
    public async Task Upgrade_adds_mailbox_preferences_and_backfills_folder_categories()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE MailAccounts (Id INTEGER PRIMARY KEY, EmailAddress TEXT NOT NULL);
                INSERT INTO MailAccounts (Id, EmailAddress) VALUES (1, 'legacy@yahoo.com');
                CREATE TABLE ArchivedEmails (
                    Id INTEGER PRIMARY KEY,
                    MailAccountId INTEGER NOT NULL,
                    FolderName TEXT NOT NULL,
                    ReceivedDate TEXT NOT NULL,
                    IsOutgoing INTEGER NOT NULL
                );
                INSERT INTO ArchivedEmails VALUES (1, 1, 'INBOX', '2026-09-17', 0);
                INSERT INTO ArchivedEmails VALUES (2, 1, 'Bulk', '2026-09-16', 0);
                INSERT INTO ArchivedEmails VALUES (3, 1, 'Custom', '2026-09-15', 1);
                INSERT INTO ArchivedEmails VALUES (4, 1, 'Gelöscht', '2026-09-14', 0);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);

        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);
        await using (var preserveCategory = connection.CreateCommand())
        {
            preserveCategory.CommandText = "UPDATE ArchivedEmails SET FolderCategory = 'Archive' WHERE Id = 1;";
            await preserveCategory.ExecuteNonQueryAsync();
        }
        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);

        await using var accountCommand = connection.CreateCommand();
        accountCommand.CommandText = "SELECT MailboxSyncLookbackDays, ExpandedMailboxCategories FROM MailAccounts WHERE Id = 1;";
        await using (var reader = await accountCommand.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
        }

        await using var emailCommand = connection.CreateCommand();
        emailCommand.CommandText = "SELECT FolderCategory FROM ArchivedEmails ORDER BY Id;";
        await using (var reader = await emailCommand.ExecuteReaderAsync())
        {
            var categories = new List<string>();
            while (await reader.ReadAsync())
                categories.Add(reader.GetString(0));
            Assert.Equal(["Archive", "Junk", "Sent", "Trash"], categories);
        }
    }

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

    [Fact]
    public async Task Upgrade_adds_credential_classification_columns_with_safe_defaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE MailAccounts (Id INTEGER PRIMARY KEY, EmailAddress TEXT NOT NULL, Provider TEXT NOT NULL);" +
                "INSERT INTO MailAccounts VALUES (1, 'legacy@corp.example', 'IMAP');";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);

        await LocalDatabaseSchemaUpgrade.ApplyAsync(context);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT MailProviderKind, CredentialKind, CredentialScope, SmtpServer, SmtpPort, SmtpUseSSL, EndpointDiscoveryStatus, ImportedDomain, PreferredIncomingAuth, PreferredOutgoingAuth FROM MailAccounts WHERE Id = 1;";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Custom", reader.GetString(0));
        Assert.Equal("Unknown", reader.GetString(1));
        Assert.Equal("Unknown", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));
        Assert.True(reader.IsDBNull(7));
        Assert.Equal("Unknown", reader.GetString(8));
        Assert.Equal("Unknown", reader.GetString(9));
    }
}
