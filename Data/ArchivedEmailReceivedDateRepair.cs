using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using MailArchiver.Models;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Data;

/// <summary>
/// One-time repair for archives created when ReceivedDate was incorrectly set to refresh time.
/// </summary>
public static class ArchivedEmailReceivedDateRepair
{
    private const string RepairKey = "received-date-from-mail-headers-v1";
    private const int BatchSize = 200;

    private static readonly Regex FirstReceivedHeader = new(
        @"^Received\s*:\s*(?<value>[^\n]*(?:\n[ \t]+[^\n]*)*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task ApplyAsync(
        MailArchiverDbContext context,
        DateTimeHelper dateTimeHelper,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var tableName = context.Database.IsNpgsql()
            ? "mail_archiver.\"DataRepairHistory\""
            : "\"DataRepairHistory\"";
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await ExecuteAsync(connection, $@"
                CREATE TABLE IF NOT EXISTS {tableName} (
                    ""RepairKey"" TEXT NOT NULL PRIMARY KEY,
                    ""AppliedAtUtc"" TEXT NOT NULL
                );", cancellationToken);

            if (await HasRunAsync(connection, tableName, cancellationToken))
                return;

            var lastId = 0;
            var repairedCount = 0;
            while (true)
            {
                var batch = await context.ArchivedEmails
                    .Where(email => email.Id > lastId)
                    .OrderBy(email => email.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                    break;

                var corrections = new List<(ArchivedEmail Email, DateTime CorrectedDate)>();
                foreach (var email in batch)
                {
                    var parsedReceivedDate = TryExtractReceivedDate(email.RawHeaders);
                    var correctedDate = parsedReceivedDate.HasValue
                        ? dateTimeHelper.ConvertToDisplayTimeZone(parsedReceivedDate.Value)
                        : email.SentDate;

                    if (email.ReceivedDate != correctedDate)
                        corrections.Add((email, correctedDate));
                }

                if (corrections.Count > 0)
                {
                    await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                    var lockedEmails = corrections
                        .Select(correction => correction.Email)
                        .Where(email => email.IsLocked)
                        .ToList();

                    // PostgreSQL's compliance trigger intentionally blocks changes to locked
                    // rows. Unlock, repair, and restore the lock in one atomic transaction so
                    // a failure can never leave protected mail unlocked.
                    foreach (var email in lockedEmails)
                        email.IsLocked = false;
                    if (lockedEmails.Count > 0)
                        await context.SaveChangesAsync(cancellationToken);

                    foreach (var correction in corrections)
                        correction.Email.ReceivedDate = correction.CorrectedDate;
                    await context.SaveChangesAsync(cancellationToken);

                    foreach (var email in lockedEmails)
                        email.IsLocked = true;
                    if (lockedEmails.Count > 0)
                        await context.SaveChangesAsync(cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    repairedCount += corrections.Count;
                }

                lastId = batch[^1].Id;
                context.ChangeTracker.Clear();
            }

            await MarkCompletedAsync(connection, tableName, cancellationToken);
            logger.LogInformation(
                "Repaired real received timestamps for {Count} existing archived emails",
                repairedCount);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    public static DateTimeOffset? TryExtractReceivedDate(string? rawHeaders)
    {
        if (string.IsNullOrWhiteSpace(rawHeaders))
            return null;

        var normalizedHeaders = rawHeaders.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var match = FirstReceivedHeader.Match(normalizedHeaders);
        if (!match.Success)
            return null;

        var headerValue = Regex.Replace(match.Groups["value"].Value, @"\n[ \t]+", " ");
        var lastSemicolon = headerValue.LastIndexOf(';');
        if (lastSemicolon < 0 || lastSemicolon == headerValue.Length - 1)
            return null;

        var dateText = headerValue[(lastSemicolon + 1)..].Trim();
        var commentStart = dateText.IndexOf('(');
        if (commentStart > 0)
            dateText = dateText[..commentStart].Trim();

        return DateTimeOffset.TryParse(
            dateText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private static async Task<bool> HasRunAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {tableName} WHERE \"RepairKey\" = @repairKey LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@repairKey";
        parameter.Value = RepairKey;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    private static async Task MarkCompletedAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO {tableName} (""RepairKey"", ""AppliedAtUtc"")
            VALUES (@repairKey, @appliedAtUtc);";

        var keyParameter = command.CreateParameter();
        keyParameter.ParameterName = "@repairKey";
        keyParameter.Value = RepairKey;
        command.Parameters.Add(keyParameter);

        var dateParameter = command.CreateParameter();
        dateParameter.ParameterName = "@appliedAtUtc";
        dateParameter.Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.Add(dateParameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
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
}
