using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Providers.Graph;
using MailArchiver.Services.Providers.Outlook;
using MailArchiver.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
namespace MailArchiver.Tests.Services;

public sealed class OutlookGraphArchiveTests
{
    [Theory]
    [InlineData(MailboxFolderCategory.Inbox, "inbox")]
    [InlineData(MailboxFolderCategory.Junk, "junkemail")]
    public async Task Visible_folder_is_saved_before_the_rest_and_refresh_does_not_duplicate(MailboxFolderCategory priority, string firstFolder)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MailArchiverDbContext(new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = new MailAccount { Name = "Outlook", EmailAddress = "test@outlook.com", Provider = ProviderType.MSA };
        db.Add(account); await db.SaveChangesAsync();
        var dates = new DateTimeHelper(Options.Create(new TimeZoneOptions()));
        var archiver = new GraphMailArchiver(db, NullLogger<GraphMailArchiver>.Instance, dates);
        var core = new EmailCoreService(db, NullLogger<EmailCoreService>.Instance, dates, Options.Create(new BatchOperationOptions()), Options.Create(new MailSyncOptions()));
        var reader = new Reader(db, firstFolder);
        var sync = new OutlookGraphMailSyncService(reader, archiver, db, null!, core);
        await sync.SyncAsync(account, null, new(7, PriorityCategory: priority));
        Assert.Equal(firstFolder, reader.Folders[0]);
        var saved = Assert.Single(await db.ArchivedEmails.AsNoTracking().ToListAsync());
        Assert.Equal("full body", saved.Body); Assert.Equal(priority, saved.FolderCategory);
        Assert.Equal(reader.Received.UtcDateTime, saved.ReceivedDate);
        Assert.Equal(reader.Sent.UtcDateTime, saved.SentDate);
        await sync.SyncAsync(account, null, new(7, PriorityCategory: priority));
        Assert.Equal(1, await db.ArchivedEmails.CountAsync());
    }

    private sealed class Reader(MailArchiverDbContext db, string firstFolder) : IOutlookGraphMailboxClient
    {
        public List<string> Folders { get; } = [];
        public DateTimeOffset Received { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public DateTimeOffset Sent { get; } = DateTimeOffset.UtcNow.AddDays(-10);
        public Task VerifyAsync(MailAccount account, CancellationToken token = default) => Task.CompletedTask;
        public GraphServiceClient CreateClient(MailAccount account) => new(new HttpClient(), new Microsoft.Kiota.Abstractions.Authentication.AnonymousAuthenticationProvider());
        public Task<List<OutlookMailboxFolder>> GetFoldersAsync(MailAccount account, CancellationToken token = default) => Task.FromResult(new List<OutlookMailboxFolder>());
        public async Task<List<Message>> ReadAsync(MailAccount account, string folderId, int days, int limit, CancellationToken token = default)
        {
            Folders.Add(folderId);
            if (folderId != firstFolder)
            {
                Assert.Equal(1, await db.ArchivedEmails.CountAsync(token)); // persistence precedes remaining network calls
                return [];
            }
            return [new Message { Id = "graph-id", InternetMessageId = "<message@test>", Subject = "subject", SentDateTime = Sent,
                ReceivedDateTime = Received, HasAttachments = false, Body = new ItemBody { ContentType = BodyType.Text, Content = "full body" },
                From = new Recipient { EmailAddress = new EmailAddress { Address = "sender@test" } },
                ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = account.EmailAddress } }] }];
        }
    }
}
