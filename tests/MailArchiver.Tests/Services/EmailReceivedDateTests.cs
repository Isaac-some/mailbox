using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Services.Providers.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;
using MimeKit;
using MailArchiver.Utilities;

namespace MailArchiver.Tests.Services;

public class EmailReceivedDateTests
{
    [Fact]
    public async Task ArchiveEmailAsync_uses_the_message_date_instead_of_the_refresh_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var account = new MailAccount
        {
            Name = "test",
            EmailAddress = "owner@example.com",
            Provider = ProviderType.IMAP,
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();

        var originalDate = new DateTimeOffset(2024, 5, 1, 8, 30, 0, TimeSpan.Zero);
        var message = new MimeMessage
        {
            MessageId = "real-received-date@example.com",
            Subject = "old message",
            Date = originalDate,
            Body = new TextPart("plain") { Text = "body" }
        };
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse(account.EmailAddress));

        var service = ServiceFactory.CreateEmailCoreService(context);
        await service.ArchiveEmailAsync(account, message, isOutgoing: false, folderName: "INBOX");

        var stored = await context.ArchivedEmails.SingleAsync();
        Assert.Equal(stored.SentDate, stored.ReceivedDate);
        Assert.Equal(new DateTime(2024, 5, 1, 10, 30, 0), stored.ReceivedDate);
    }

    [Fact]
    public async Task ArchiveEmailAsync_prefers_the_IMAP_server_received_date()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var account = new MailAccount
        {
            Name = "test",
            EmailAddress = "owner@example.com",
            Provider = ProviderType.IMAP,
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();

        var sentDate = new DateTimeOffset(2024, 5, 1, 8, 30, 0, TimeSpan.Zero);
        var receivedDate = new DateTimeOffset(2024, 5, 3, 9, 45, 0, TimeSpan.Zero);
        var message = CreateMessage("server-date@example.com", "delayed message", sentDate, account.EmailAddress);

        var service = ServiceFactory.CreateEmailCoreService(context);
        await service.ArchiveEmailAsync(
            account,
            message,
            isOutgoing: false,
            folderName: "INBOX",
            receivedDate: receivedDate);

        var stored = await context.ArchivedEmails.SingleAsync();
        Assert.Equal(new DateTime(2024, 5, 1, 10, 30, 0), stored.SentDate);
        Assert.Equal(new DateTime(2024, 5, 3, 11, 45, 0), stored.ReceivedDate);
    }

    [Fact]
    public async Task Inbox_sort_uses_received_date_descending_with_a_stable_tie_breaker()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var account = new MailAccount
        {
            Name = "test",
            EmailAddress = "owner@example.com",
            Provider = ProviderType.IMAP,
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();

        var service = ServiceFactory.CreateEmailCoreService(context);
        var sentDate = new DateTimeOffset(2024, 5, 1, 8, 30, 0, TimeSpan.Zero);
        var sameReceivedDate = new DateTimeOffset(2024, 5, 3, 9, 45, 0, TimeSpan.Zero);

        await service.ArchiveEmailAsync(
            account,
            CreateMessage("older@example.com", "older", sentDate, account.EmailAddress),
            false,
            "INBOX",
            sameReceivedDate.AddDays(-1));
        await service.ArchiveEmailAsync(
            account,
            CreateMessage("tie-one@example.com", "tie one", sentDate, account.EmailAddress),
            false,
            "INBOX",
            sameReceivedDate);
        await service.ArchiveEmailAsync(
            account,
            CreateMessage("tie-two@example.com", "tie two", sentDate, account.EmailAddress),
            false,
            "INBOX",
            sameReceivedDate);

        var (emails, _) = await service.SearchEmailsAsync(
            string.Empty,
            null,
            null,
            account.Id,
            "INBOX",
            false,
            0,
            10,
            sortBy: "ReceivedDate",
            sortOrder: "desc");

        Assert.Equal(
            new[] { "tie-two@example.com", "tie-one@example.com", "older@example.com" },
            emails.Select(email => email.MessageId));
    }

    [Fact]
    public void Graph_received_date_prefers_receivedDateTime_over_sentDateTime()
    {
        var sentDate = new DateTimeOffset(2024, 5, 1, 8, 30, 0, TimeSpan.Zero);
        var receivedDate = sentDate.AddHours(2);
        var message = new Message
        {
            SentDateTime = sentDate,
            ReceivedDateTime = receivedDate
        };

        Assert.Equal(receivedDate, GraphMailArchiver.ResolveReceivedDate(message));
    }

    [Fact]
    public async Task Historical_repair_restores_received_headers_once_and_falls_back_to_sent_date()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var account = new MailAccount
        {
            Name = "test",
            EmailAddress = "owner@example.com",
            Provider = ProviderType.IMAP,
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();

        var sentDate = new DateTime(2024, 5, 1, 10, 0, 0);
        context.ArchivedEmails.AddRange(
            CreateArchivedEmail(
                account.Id,
                "repair-header@example.com",
                sentDate,
                "Received: from mx.example.com; Wed, 1 May 2024 12:30:00 +0000 (UTC)\r\n"),
            CreateArchivedEmail(account.Id, "repair-fallback@example.com", sentDate, null));
        await context.SaveChangesAsync();

        var helper = new DateTimeHelper(Options.Create(
            new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" }));
        await ArchivedEmailReceivedDateRepair.ApplyAsync(
            context,
            helper,
            NullLogger.Instance);

        var repaired = await context.ArchivedEmails.OrderBy(email => email.Id).ToListAsync();
        Assert.Equal(new DateTime(2024, 5, 1, 14, 30, 0), repaired[0].ReceivedDate);
        Assert.Equal(sentDate, repaired[1].ReceivedDate);

        repaired[0].ReceivedDate = new DateTime(2030, 1, 1);
        await context.SaveChangesAsync();
        await ArchivedEmailReceivedDateRepair.ApplyAsync(
            context,
            helper,
            NullLogger.Instance);

        Assert.Equal(
            new DateTime(2030, 1, 1),
            (await context.ArchivedEmails.FindAsync(repaired[0].Id))!.ReceivedDate);
    }

    private static MimeMessage CreateMessage(
        string messageId,
        string subject,
        DateTimeOffset sentDate,
        string recipient)
    {
        var message = new MimeMessage
        {
            MessageId = messageId,
            Subject = subject,
            Date = sentDate,
            Body = new TextPart("plain") { Text = "body" }
        };
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse(recipient));
        return message;
    }

    private static ArchivedEmail CreateArchivedEmail(
        int accountId,
        string messageId,
        DateTime sentDate,
        string? rawHeaders)
    {
        return new ArchivedEmail
        {
            MailAccountId = accountId,
            MessageId = messageId,
            Subject = "historical",
            Body = "body",
            HtmlBody = string.Empty,
            From = "sender@example.com",
            To = "owner@example.com",
            Cc = string.Empty,
            Bcc = string.Empty,
            SentDate = sentDate,
            ReceivedDate = new DateTime(2026, 8, 27, 10, 0, 0),
            FolderName = "INBOX",
            RawHeaders = rawHeaders
        };
    }
}
