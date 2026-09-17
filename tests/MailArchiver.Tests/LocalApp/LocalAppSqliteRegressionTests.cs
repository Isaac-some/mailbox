using MailArchiver.Auth.Options;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.LocalApp;

public class LocalAppSqliteRegressionTests
{
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
