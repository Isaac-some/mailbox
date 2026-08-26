using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Data;

public static class LocalDatabaseSchemaUpgrade
{
    public static async Task ApplyAsync(MailArchiverDbContext context, CancellationToken cancellationToken = default)
    {
        if (!context.Database.IsSqlite())
            return;

        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "OAuthGrantedScopes", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "OAuthRedirectUri", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "MailProviderKind", cancellationToken);
            if (await HasColumnAsync(connection, "MailAccounts", "Provider", cancellationToken))
            {
                await ExecuteAsync(connection, @"
                UPDATE ""MailAccounts""
                SET ""MailProviderKind"" = CASE
                    WHEN ""Provider"" = 'MSA' THEN 'Outlook'
                    WHEN ""Provider"" = 'IMAP' AND (LOWER(""EmailAddress"") LIKE '%@gmail.com' OR LOWER(""EmailAddress"") LIKE '%@googlemail.com') THEN 'Gmail'
                    WHEN ""Provider"" = 'IMAP' AND LOWER(""EmailAddress"") LIKE '%@yahoo.%' THEN 'Yahoo'
                    WHEN ""Provider"" = 'IMAP' AND (LOWER(""EmailAddress"") LIKE '%@gmx.com' OR LOWER(""EmailAddress"") LIKE '%@gmx.net' OR LOWER(""EmailAddress"") LIKE '%@gmx.de') THEN 'Gmx'
                    ELSE ""MailProviderKind""
                END
                WHERE ""MailProviderKind"" IS NULL;", cancellationToken);
            }

            await ExecuteAsync(connection, @"
                CREATE TABLE IF NOT EXISTS ""OutboundMailTasks"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_OutboundMailTasks"" PRIMARY KEY AUTOINCREMENT,
                    ""CreatedByUserId"" INTEGER NOT NULL,
                    ""Name"" TEXT NOT NULL,
                    ""CreatedAtUtc"" TEXT NOT NULL,
                    CONSTRAINT ""FK_OutboundMailTasks_Users_CreatedByUserId""
                        FOREIGN KEY (""CreatedByUserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                );", cancellationToken);
            await ExecuteAsync(connection, @"
                CREATE TABLE IF NOT EXISTS ""OutboundMailTaskItems"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_OutboundMailTaskItems"" PRIMARY KEY AUTOINCREMENT,
                    ""OutboundMailTaskId"" INTEGER NOT NULL,
                    ""MailAccountId"" INTEGER NOT NULL,
                    ""CsvRowNumber"" INTEGER NOT NULL,
                    ""ScheduledAtUtc"" TEXT NOT NULL,
                    ""Recipient"" TEXT NOT NULL,
                    ""Subject"" TEXT NOT NULL,
                    ""Body"" TEXT NOT NULL,
                    ""Status"" TEXT NOT NULL,
                    ""StartedAtUtc"" TEXT NULL,
                    ""CompletedAtUtc"" TEXT NULL,
                    ""MessageId"" TEXT NULL,
                    ""SentCopySaved"" INTEGER NULL,
                    ""ErrorMessage"" TEXT NULL,
                    CONSTRAINT ""FK_OutboundMailTaskItems_OutboundMailTasks_OutboundMailTaskId""
                        FOREIGN KEY (""OutboundMailTaskId"") REFERENCES ""OutboundMailTasks"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_OutboundMailTaskItems_MailAccounts_MailAccountId""
                        FOREIGN KEY (""MailAccountId"") REFERENCES ""MailAccounts"" (""Id"") ON DELETE RESTRICT
                );", cancellationToken);
            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_OutboundMailTasks_CreatedByUserId\" ON \"OutboundMailTasks\" (\"CreatedByUserId\");",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_OutboundMailTaskItems_OutboundMailTaskId\" ON \"OutboundMailTaskItems\" (\"OutboundMailTaskId\");",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_OutboundMailTaskItems_MailAccountId\" ON \"OutboundMailTaskItems\" (\"MailAccountId\");",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_OutboundMailTaskItems_Status_ScheduledAtUtc\" ON \"OutboundMailTaskItems\" (\"Status\", \"ScheduledAtUtc\");",
                cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNullableTextColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, table, column, cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
