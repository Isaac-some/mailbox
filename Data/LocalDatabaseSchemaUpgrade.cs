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
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "SmtpServer", cancellationToken);
            await EnsureNullableIntegerColumnAsync(connection, "MailAccounts", "SmtpPort", cancellationToken);
            await EnsureNullableBooleanColumnAsync(connection, "MailAccounts", "SmtpUseSSL", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "EndpointDiscoveryStatus", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "EndpointDiscoveryLastCheckedAt", cancellationToken);
            await EnsureTextColumnAsync(connection, "MailAccounts", "CredentialKind", "Unknown", cancellationToken);
            await EnsureTextColumnAsync(connection, "MailAccounts", "CredentialScope", "Unknown", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "CredentialDetectionStatus", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "CredentialLastCheckedAt", cancellationToken);
            await EnsureNullableTextColumnAsync(connection, "MailAccounts", "ImportedDomain", cancellationToken);
            await EnsureTextColumnAsync(connection, "MailAccounts", "PreferredIncomingAuth", "Unknown", cancellationToken);
            await EnsureTextColumnAsync(connection, "MailAccounts", "PreferredOutgoingAuth", "Unknown", cancellationToken);
            await EnsureIntegerColumnAsync(connection, "MailAccounts", "MailboxSyncLookbackDays", 7, cancellationToken);
            await EnsureIntegerColumnAsync(connection, "MailAccounts", "ExpandedMailboxCategories", 0, cancellationToken);
            if (await HasColumnAsync(connection, "MailAccounts", "Provider", cancellationToken))
            {
                await ExecuteAsync(connection, @"
                UPDATE ""MailAccounts""
                SET ""MailProviderKind"" = CASE
                    WHEN ""Provider"" = 'MSA' THEN 'Outlook'
                    WHEN ""Provider"" = 'IMAP' AND (LOWER(""EmailAddress"") LIKE '%@gmail.com' OR LOWER(""EmailAddress"") LIKE '%@googlemail.com') THEN 'Gmail'
                    WHEN ""Provider"" = 'IMAP' AND LOWER(""EmailAddress"") LIKE '%@yahoo.%' THEN 'Yahoo'
                    WHEN ""Provider"" = 'IMAP' AND (LOWER(""EmailAddress"") LIKE '%@gmx.com' OR LOWER(""EmailAddress"") LIKE '%@gmx.net' OR LOWER(""EmailAddress"") LIKE '%@gmx.de') THEN 'Gmx'
                    WHEN ""MailProviderKind"" IS NULL THEN 'Custom'
                    ELSE ""MailProviderKind""
                END
                WHERE ""MailProviderKind"" IS NULL;", cancellationToken);
            }

            if (await HasTableAsync(connection, "ArchivedEmails", cancellationToken))
            {
                var needsFolderCategoryBackfill = !await HasColumnAsync(
                    connection, "ArchivedEmails", "FolderCategory", cancellationToken);
                await EnsureTextColumnAsync(connection, "ArchivedEmails", "FolderCategory", "Other", cancellationToken);
                if (needsFolderCategoryBackfill)
                {
                    await ExecuteAsync(connection, @"
                        UPDATE ""ArchivedEmails""
                        SET ""FolderCategory"" = CASE
                            WHEN LOWER(""FolderName"") IN ('inbox', '收件箱', 'posteingang') THEN 'Inbox'
                            WHEN LOWER(""FolderName"") IN ('bulk', 'junk', 'junk email', 'junk e-mail', 'spam', 'spamverdacht', '垃圾邮件') THEN 'Junk'
                            WHEN LOWER(""FolderName"") LIKE '%sent%' OR LOWER(""FolderName"") LIKE '%已发送%' OR LOWER(""FolderName"") LIKE '%gesendet%' THEN 'Sent'
                            WHEN LOWER(""FolderName"") LIKE '%trash%' OR LOWER(""FolderName"") LIKE '%deleted%' OR LOWER(""FolderName"") LIKE '%已删除%'
                                OR LOWER(""FolderName"") IN ('gelöscht', 'geloescht', 'geloscht') THEN 'Trash'
                            WHEN LOWER(""FolderName"") LIKE '%draft%' OR LOWER(""FolderName"") LIKE '%草稿%' THEN 'Drafts'
                            WHEN LOWER(""FolderName"") LIKE '%archive%' OR LOWER(""FolderName"") LIKE '%归档%' THEN 'Archive'
                            WHEN ""IsOutgoing"" <> 0 THEN 'Sent'
                            ELSE 'Other'
                        END;", cancellationToken);
                }
                await ExecuteAsync(connection,
                    "CREATE INDEX IF NOT EXISTS \"IX_ArchivedEmails_Account_Category_ReceivedDate\" ON \"ArchivedEmails\" (\"MailAccountId\", \"FolderCategory\", \"ReceivedDate\");",
                    cancellationToken);
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

    private static async Task EnsureTextColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        string defaultValue,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, table, column, cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        var escapedDefault = defaultValue.Replace("'", "''", StringComparison.Ordinal);
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT NOT NULL DEFAULT '{escapedDefault}';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNullableIntegerColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, table, column, cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" INTEGER NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIntegerColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        int defaultValue,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, table, column, cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" INTEGER NOT NULL DEFAULT {defaultValue};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNullableBooleanColumnAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, table, column, cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" INTEGER NULL;";
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

    private static async Task<bool> HasTableAsync(
        System.Data.Common.DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }
}
