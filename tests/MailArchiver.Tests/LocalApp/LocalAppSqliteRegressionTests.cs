using MailArchiver.Auth.Options;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Services;
using MailArchiver.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using MailArchiver.ViewModels;
using MailArchiver.Services.Shared;
using MailArchiver.Services.Providers.Eml;
using Microsoft.Extensions.DependencyInjection;

namespace MailArchiver.Tests.LocalApp;

public class LocalAppSqliteRegressionTests
{
    [Fact]
    public async Task Local_import_persists_message_and_deduplicates_across_header_timezones()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailArchiverDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new DateTimeHelper(Options.Create(new TimeZoneOptions { StorageTimeZoneId = "Etc/UTC" })));
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
        await context.Database.EnsureCreatedAsync();
        var account = await SeedAccountAsync(context);
        var message = new MimeMessage { Subject = "local import", MessageId = "first@test",
            Date = new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.FromHours(8)) };
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse(account.EmailAddress));
        message.Body = new TextPart("plain") { Text = "local import" };
        var importer = new MailImporter(provider, NullLogger<MailImporter>.Instance,
            new EmlAttachmentCollector(NullLogger<EmlAttachmentCollector>.Instance));
        var result = await importer.ImportEmailToDatabase(message, account, "test", "INBOX");
        Assert.True(result.Success, result.Error);
        message.MessageId = "different-id@test";
        message.Date = message.Date.ToOffset(TimeSpan.FromHours(-5)).AddSeconds(1);
        Assert.True((await importer.ImportEmailToDatabase(message, account, "test", "INBOX")).AlreadyExists);
        Assert.Equal(1, await context.ArchivedEmails.CountAsync());
    }

    [Fact]
    public async Task Local_attachment_bytes_survive_reopen_and_eml_export()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        var payload = System.Text.Encoding.UTF8.GetBytes("中文附件\nexact bytes\0\u0001");
        var message = new MimeMessage { Subject = "attachment round trip", MessageId = "round-trip@test", Date = DateTimeOffset.UtcNow };
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse(account.EmailAddress));
        var body = new BodyBuilder { TextBody = "attachment verification" };
        body.Attachments.Add("原文件.bin", payload);
        message.Body = body.ToMessageBody();
        var service = CreateService(database.Context);
        Assert.True(await service.ArchiveEmailAsync(account, message, false, "INBOX"));
        database.Context.ChangeTracker.Clear();
        var archived = await database.Context.ArchivedEmails.Include(e => e.Attachments).SingleAsync();
        Assert.Equal(payload, Assert.Single(archived.Attachments).Content);
        var exported = await service.ExportEmailsAsync(new ExportViewModel { EmailId = archived.Id });
        using var stream = new MemoryStream(exported);
        var mime = await MimeMessage.LoadAsync(stream);
        var file = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        using var decoded = new MemoryStream();
        await file.Content.DecodeToAsync(decoded);
        Assert.Equal(payload, decoded.ToArray());
    }

    [Fact]
    public async Task Refresh_repairs_legacy_empty_attachment_without_duplicating_the_email()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        var email = BuildEmail(account, "old cache", DateTime.UtcNow);
        var payload = new byte[] { 0, 1, 2, 254, 255 };
        email.HasAttachments = true;
        email.Attachments.Add(new EmailAttachment { FileName = "restore.bin", ContentType = "application/octet-stream", Size = payload.Length });
        database.Context.ArchivedEmails.Add(email);
        await database.Context.SaveChangesAsync();
        var message = new MimeMessage { Subject = email.Subject, MessageId = email.MessageId, Date = DateTimeOffset.UtcNow };
        message.From.Add(MailboxAddress.Parse(email.From));
        message.To.Add(MailboxAddress.Parse(email.To));
        var body = new BodyBuilder { TextBody = email.Body };
        body.Attachments.Add("restore.bin", payload);
        message.Body = body.ToMessageBody();
        var service = CreateService(database.Context);
        Assert.False(await service.ArchiveEmailAsync(account, message, false, "INBOX"));
        database.Context.ChangeTracker.Clear();
        var saved = await database.Context.ArchivedEmails.Include(e => e.Attachments).SingleAsync();
        Assert.Equal(email.Id, saved.Id);
        Assert.Equal(payload, Assert.Single(saved.Attachments).Content);
    }

    [Fact]
    public async Task Existing_sqlite_archive_adds_only_redacted_failure_records_and_expires_old_rows()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        await database.Context.Database.ExecuteSqlRawAsync("DROP TABLE \"SyncFailureRecords\"");
        await LocalDatabaseSchemaUpgrade.ApplyAsync(database.Context);

        var recorder = new SyncFailureRecorder(database.Context);
        var failure = MailConnectionFailurePolicy.Classify(
            new MailCredentialFormatException("secret password and proxy.example.com"));
        await recorder.RecordAsync("test-job", account.Id, "VerifyCredential", failure);
        var record = await database.Context.SyncFailureRecords.AsNoTracking().SingleAsync();
        Assert.Equal(MailFailureCode.GmailCredentialFormatInvalid.ToString(), record.ErrorCode);
        Assert.Equal(MailFailureStage.CredentialValidation.ToString(), record.FailureStage);
        Assert.DoesNotContain("secret", string.Join(" ", record.JobId, record.Operation,
            record.ErrorCode, record.FailureStage, record.ExceptionCategory));
        Assert.DoesNotContain("proxy.example.com", string.Join(" ", record.JobId, record.Operation,
            record.ErrorCode, record.FailureStage, record.ExceptionCategory));

        await database.Context.SyncFailureRecords.ExecuteUpdateAsync(setters =>
            setters.SetProperty(item => item.OccurredAtUtc, DateTime.UtcNow.AddDays(-8)));
        var removed = await database.Context.SyncFailureRecords
            .Where(item => item.OccurredAtUtc < DateTime.UtcNow.AddDays(-7))
            .ExecuteDeleteAsync();
        Assert.Equal(1, removed);
        Assert.Empty(await database.Context.SyncFailureRecords.ToListAsync());
    }

    [Fact]
    public async Task Search_finds_subject_on_sqlite_without_postgresql_functions()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        database.Context.ArchivedEmails.AddRange(
            BuildEmail(account, "333", DateTime.UtcNow.AddMinutes(-1)),
            BuildEmail(account, "unrelated", DateTime.UtcNow.AddMinutes(-2)));
        await database.Context.SaveChangesAsync();

        var service = CreateService(database.Context);
        var (emails, total) = await service.SearchEmailsAsync(
            "333",
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow,
            account.Id,
            "INBOX",
            false,
            0,
            20,
            new List<int> { account.Id },
            "ReceivedDate",
            "desc",
            useReceivedDateForRange: true);

        Assert.Equal(1, total);
        Assert.Equal("333", Assert.Single(emails).Subject);
    }

    [Fact]
    public async Task Mailbox_cap_keeps_most_recently_archived_message_despite_future_sent_header()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        database.Context.ArchivedEmails.AddRange(
            BuildEmail(
                account,
                "old-with-future-header",
                DateTime.UtcNow.AddDays(-1),
                DateTime.UtcNow.AddYears(10)),
            BuildEmail(
                account,
                "newly-received",
                DateTime.UtcNow,
                DateTime.UtcNow.AddDays(-10)));
        await database.Context.SaveChangesAsync();

        var service = CreateService(
            database.Context,
            new MailSyncOptions { MaxStoredEmailsPerAccount = 1 });
        await service.EnforceLocalEmailLimitAsync(account.Id);

        var remaining = await database.Context.ArchivedEmails
            .AsNoTracking()
            .Select(email => email.Subject)
            .SingleAsync();
        Assert.Equal("newly-received", remaining);
    }

    [Fact]
    public async Task Category_limits_keep_each_mailbox_category_independently()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        account.ExpandMailboxCategory(MailboxFolderCategory.Sent);
        database.Context.ArchivedEmails.AddRange(
            BuildEmail(account, "inbox-new", DateTime.UtcNow, category: MailboxFolderCategory.Inbox),
            BuildEmail(account, "inbox-old", DateTime.UtcNow.AddMinutes(-1), category: MailboxFolderCategory.Inbox),
            BuildEmail(account, "sent-1", DateTime.UtcNow, category: MailboxFolderCategory.Sent),
            BuildEmail(account, "sent-2", DateTime.UtcNow.AddMinutes(-1), category: MailboxFolderCategory.Sent),
            BuildEmail(account, "sent-3", DateTime.UtcNow.AddMinutes(-2), category: MailboxFolderCategory.Sent),
            BuildEmail(account, "sent-4", DateTime.UtcNow.AddMinutes(-3), category: MailboxFolderCategory.Sent),
            BuildEmail(account, "junk", DateTime.UtcNow, category: MailboxFolderCategory.Junk));
        await database.Context.SaveChangesAsync();

        var service = CreateService(database.Context, new MailSyncOptions
        {
            InitialMessagesPerCategory = 1,
            ExpandedMessagesPerCategory = 3
        });
        await service.EnforceMailboxCategoryLimitsAsync(account, 7);

        var remaining = await database.Context.ArchivedEmails
            .AsNoTracking()
            .GroupBy(email => email.FolderCategory)
            .ToDictionaryAsync(group => group.Key, group => group.Count());
        Assert.Equal(1, remaining[MailboxFolderCategory.Inbox]);
        Assert.Equal(3, remaining[MailboxFolderCategory.Sent]);
        Assert.Equal(1, remaining[MailboxFolderCategory.Junk]);
    }

    [Fact]
    public async Task Search_filters_normalized_folder_category_and_orders_by_received_date()
    {
        await using var database = await CreateDatabaseAsync();
        var account = await SeedAccountAsync(database.Context);
        database.Context.ArchivedEmails.AddRange(
            BuildEmail(account, "older-junk", DateTime.UtcNow.AddMinutes(-2), category: MailboxFolderCategory.Junk),
            BuildEmail(account, "newer-junk", DateTime.UtcNow.AddMinutes(-1), category: MailboxFolderCategory.Junk),
            BuildEmail(account, "inbox", DateTime.UtcNow, category: MailboxFolderCategory.Inbox));
        await database.Context.SaveChangesAsync();

        var service = CreateService(database.Context);
        var (emails, total) = await service.SearchEmailsAsync(
            null!, null, null, account.Id, null!, null, 0, 20,
            sortBy: "ReceivedDate",
            sortOrder: "desc",
            folderCategory: MailboxFolderCategory.Junk);

        Assert.Equal(2, total);
        Assert.Equal(new[] { "newer-junk", "older-junk" }, emails.Select(email => email.Subject));
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestDatabase(connection, context);
    }

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext context)
    {
        var account = new MailAccount
        {
            Name = "Yahoo test",
            EmailAddress = "regression@yahoo.com",
            Provider = ProviderType.IMAP,
            IsEnabled = true,
            LastSync = DateTime.UtcNow
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static ArchivedEmail BuildEmail(
        MailAccount account,
        string subject,
        DateTime receivedDate,
        DateTime? sentDate = null,
        MailboxFolderCategory category = MailboxFolderCategory.Inbox) =>
        new()
        {
            MailAccountId = account.Id,
            MailAccount = account,
            MessageId = $"{Guid.NewGuid():N}@test",
            Subject = subject,
            From = "sender@example.com",
            To = account.EmailAddress,
            Cc = string.Empty,
            Bcc = string.Empty,
            Body = subject,
            HtmlBody = string.Empty,
            SentDate = sentDate ?? receivedDate,
            ReceivedDate = receivedDate,
            IsOutgoing = false,
            HasAttachments = false,
            FolderName = category == MailboxFolderCategory.Inbox ? "INBOX" : category.ToDisplayName(),
            FolderCategory = category
        };

    private static EmailCoreService CreateService(
        MailArchiverDbContext context,
        MailSyncOptions? mailSyncOptions = null) =>
        new(
            context,
            NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Options.Create(new TimeZoneOptions { StorageTimeZoneId = "Asia/Shanghai" })),
            Options.Create(new BatchOperationOptions { BatchSize = 50 }),
            Options.Create(mailSyncOptions ?? new MailSyncOptions()));

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, MailArchiverDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public MailArchiverDbContext Context { get; }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
