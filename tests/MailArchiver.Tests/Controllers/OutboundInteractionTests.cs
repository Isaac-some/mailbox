using System.Text.Json;
using MailArchiver.Controllers;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Controllers;

public sealed class OutboundInteractionTests
{
    [Fact]
    public async Task Pending_sender_is_searchable_even_beyond_the_first_page_and_scope_is_enforced()
    {
        await using var f = await Fixture.Create();
        var c = f.Tasks();
        var first = Json(await c.SearchSenders(null));
        Assert.Equal(50, first.GetProperty("accounts").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var found = Json(await c.SearchSenders("SENDER060@GMX.ES"));
        Assert.Single(found.GetProperty("accounts").EnumerateArray());
        Assert.False(found.GetProperty("accounts")[0].GetProperty("canSend").GetBoolean());
        Assert.Empty(Json(await c.SearchSenders("private")).GetProperty("accounts").EnumerateArray());
        var compose = Assert.IsType<ComposeMailViewModel>(Assert.IsType<ViewResult>(await f.Compose().Index(f.LastId, null)).Model);
        Assert.Equal(f.LastId, Assert.Single(compose.AccountIds));
    }

    [Fact]
    public async Task Selection_accepts_pending_credentials_but_rejects_disabled_foreign_and_import_only_accounts()
    {
        await using var f = await Fixture.Create();
        var c = f.Tasks();
        var result = Json(await c.PrepareSenders(new() { SelectedAccountIds = [f.LastId, f.LastId] }, default));
        Assert.Equal(1, result.GetProperty("queued").GetInt32());
        var queued = Assert.Single(f.Queue.Requests);
        Assert.Equal(f.LastId, queued.Id);
        Assert.Equal(1, queued.Actor);
        Assert.Equal(MailSyncRequestKind.PrepareSending, queued.Kind);
        Assert.IsType<BadRequestObjectResult>(await c.PrepareSenders(new() { SelectedAccountIds = [f.ForeignId] }, default));
        Assert.IsType<BadRequestObjectResult>(await c.PrepareSenders(new() { SelectedAccountIds = [f.DisabledId] }, default));
        Assert.IsType<BadRequestObjectResult>(await c.PrepareSenders(new() { SelectedAccountIds = [f.ImportId] }, default));
    }

    [Fact]
    public async Task Csv_match_and_export_keep_pending_accounts_and_only_include_authorized_senders()
    {
        await using var f = await Fixture.Create();
        var c = f.Tasks();
        var match = Json(await c.PreviewSenderSelection(new() { SourceText = "SENDER060@GMX.ES private@gmx.es missing@gmx.es" }, default));
        Assert.Equal(1, match.GetProperty("matchedCount").GetInt32());
        Assert.Equal(f.LastId, match.GetProperty("matchedAccountIds")[0].GetInt32());
        var download = Assert.IsType<FileContentResult>(await c.DownloadSenderCsv(new() { SelectedAccountIds = [f.LastId] }, default));
        Assert.Contains("sender060@gmx.es", System.Text.Encoding.UTF8.GetString(download.FileContents));
    }

    [Fact]
    public async Task Csv_task_can_queue_a_pending_sender_and_future_schedule_survives()
    {
        await using var f = await Fixture.Create();
        var text = "发件邮箱,收件邮箱,主题,正文,时间\nsender060@gmx.es,target@example.com,subject,body,2099-01-01 12:00\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var result = await f.Tasks().Import(new() { CsvFile = new FormFile(stream, 0, stream.Length, "CsvFile", "future.csv"), TimingMode = OutboundMailTaskTimingMode.UseCsvSchedule }, default);
        Assert.IsType<RedirectToActionResult>(result);
        var item = await f.Db.OutboundMailTaskItems.SingleAsync();
        Assert.Equal(f.LastId, item.MailAccountId);
        Assert.Equal(OutboundMailTaskItemStatus.Pending, item.Status);
        Assert.True(item.ScheduledAtUtc.Year == 2099);
    }

    [Fact]
    public async Task Multi_sender_partial_failure_removes_sent_accounts_preserves_draft_and_attachments()
    {
        await using var f = await Fixture.Create();
        f.Outbound.FailId = f.LastId;
        var c = f.Compose();
        var model = new ComposeMailViewModel { AccountIds = [f.FirstId, f.LastId], To = "recipient@example.com", Subject = "subject", Body = "body" };
        using var stream = new MemoryStream([1, 2, 3]);
        model.Attachments = [new FormFile(stream, 0, 3, "Attachments", "note.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" }];
        var result = Assert.IsType<ViewResult>(await c.Index(model, default));
        var returned = Assert.IsType<ComposeMailViewModel>(result.Model);
        Assert.Equal(f.LastId, Assert.Single(returned.AccountIds));
        Assert.Single(returned.SendResults);
        Assert.Equal("body", returned.Body);
        Assert.Equal(2, f.Outbound.Messages.Count);
        Assert.All(f.Outbound.Messages, m => Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(m.Attachments).Content));
    }

    [Fact]
    public async Task Compose_rejects_multiple_recipients_and_foreign_sender_before_sending()
    {
        await using var f = await Fixture.Create();
        var c = f.Compose();
        Assert.IsType<ViewResult>(await c.Index(new() { AccountIds = [f.FirstId], To = "one@example.com,two@example.com", Subject = "subject", Body = "body" }, default));
        Assert.False(c.ModelState.IsValid);
        Assert.Empty(f.Outbound.Messages);
        c = f.Compose();
        Assert.IsType<ViewResult>(await c.Index(new() { AccountIds = [f.ForeignId], To = "one@example.com", Subject = "subject", Body = "body" }, default));
        Assert.False(c.ModelState.IsValid);
        Assert.Empty(f.Outbound.Messages);
    }

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MailArchiverDbContext Db { get; }
        public Outbound Outbound { get; } = new();
        public Queue Queue { get; } = new();
        public int FirstId, LastId, ForeignId, DisabledId, ImportId;
        private Fixture(SqliteConnection connection, MailArchiverDbContext db) { _connection = connection; Db = db; }
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new MailArchiverDbContext(new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { Username = "test", Email = "test@example.com" };
            var other = new User { Username = "other", Email = "other@example.com" };
            db.Users.AddRange(user, other); await db.SaveChangesAsync();
            var fixture = new Fixture(connection, db);
            MailAccount Account(string email, User owner, bool enabled = true, ProviderType provider = ProviderType.IMAP)
            {
                var account = new MailAccount { Name = email, EmailAddress = email, IsEnabled = enabled, Provider = provider, MailProviderKind = MailProviderKind.Custom, Password = "synthetic", CredentialDetectionStatus = "PendingVerification", LastSync = DateTime.UnixEpoch };
                db.UserMailAccounts.Add(new UserMailAccount { User = owner, MailAccount = account }); return account;
            }
            var accounts = Enumerable.Range(0, 61).Select(i => Account($"sender{i:000}@gmx.es", user)).ToList();
            var foreign = Account("private@gmx.es", other); var disabled = Account("disabled@gmx.es", user, false); var imported = Account("import@gmx.es", user, provider:ProviderType.IMPORT);
            await db.SaveChangesAsync(); fixture.FirstId = accounts[0].Id; fixture.LastId = accounts[^1].Id;
            fixture.ForeignId = foreign.Id; fixture.DisabledId = disabled.Id; fixture.ImportId = imported.Id;
            return fixture;
        }
        public OutboundMailTasksController Tasks() => Setup(new OutboundMailTasksController(Db, new Auth(), Outbound, Options.Create(new OutboundMailOptions { Enabled = true }), Options.Create(new TimeZoneOptions()), Queue));
        public OutboundMailController Compose() => Setup(new OutboundMailController(Db, new Auth(), Outbound, Options.Create(new OutboundMailOptions { Enabled = true }), NullLogger<OutboundMailController>.Instance));
        private static T Setup<T>(T c) where T : Controller { c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }; c.TempData = new TempDataDictionary(c.HttpContext, new TempData()); return c; }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
    private sealed class TempData : ITempDataProvider
    { public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(); public void SaveTempData(HttpContext context, IDictionary<string, object> values) {} }
    private sealed class Outbound : IOutboundMailService
    {
        public int FailId; public List<OutboundMailMessage> Messages = [];
        public bool CanSend(MailAccount account) => account.CredentialDetectionStatus != "PendingVerification";
        public Task<OutboundMailResult> SendAsync(MailAccount account, OutboundMailMessage message, CancellationToken cancellationToken = default)
        { Messages.Add(message); return account.Id == FailId ? Task.FromException<OutboundMailResult>(new IOException()) : Task.FromResult(new OutboundMailResult(true, "test-id")); }
    }
    private sealed class Queue : IOnDemandMailSyncQueue
    {
        public List<(int Id, int? Actor, MailSyncRequestKind Kind)> Requests = [];
        public MailSyncQueueStatus Enqueue(int id, MailSyncRequestPriority priority, MailSyncRequestKind kind = MailSyncRequestKind.Synchronize, MailSyncRequestOptions? options = null)
        { Requests.Add((id, options?.RequestedByUserId, kind)); return new(id, MailSyncQueueState.Queued, kind, priority); }
        public MailSyncQueueStatus GetStatus(int id) => new(id, MailSyncQueueState.NotQueued, null, null);
    }
    private sealed class Auth : IAuthenticationService
    {
        public bool IsAuthenticationRequired() => false; public bool ValidateCredentials(string username, string password) => false;
        public Task StartUserSessionAsync(User user, bool rememberMe = false) => Task.CompletedTask;
        public void SignOut(HttpContext context) {} public bool IsAuthenticated(HttpContext context) => true;
        public string GetCurrentUserDisplayName(HttpContext context) => "test"; public int? GetCurrentUserId(HttpContext context) => 1;
        public bool IsCurrentUserAdmin(HttpContext context) => false; public bool IsCurrentUserSelfManager(HttpContext context) => true;
    }
}
