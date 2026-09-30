using System.Net;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public class UpstreamMailboxSyncServiceTests
{
    [Fact]
    public async Task Invalid_upstream_rows_are_skipped_while_valid_rows_and_cursor_are_committed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StubHandler("""
            {"data":{"total":2,"serverTime":"2026-09-03T08:30:00Z","items":[
              {"email":"person@yahoo.com","credential":"valid-code"},
              {"email":"bad@yahoo.com","credential":" \t "}
            ]}}
            """);
        var cursor = new FakeCursorStore();
        var service = new UpstreamMailboxSyncService(new StubHttpClientFactory(handler), fixture.Intake,
            cursor,
            Options.Create(new UpstreamMailboxSyncOptions { Enabled = true, Endpoint = "https://platform.example/mailboxes", BearerToken = "test-token" }),
            NullLogger<UpstreamMailboxSyncService>.Instance);
        var result = await service.PullAsync(fixture.UserId);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Rejected);
        Assert.Contains("跳过 1 个", result.Summary);
        Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Equal("2026-09-03T08:30:00Z", cursor.Cursor);
    }

    [Fact]
    public async Task PullAsync_reads_the_four_field_contract_and_fully_updates_an_existing_account()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Intake.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake("person@outlook.com", "old", "old.example", "old-client"),
            enabled: true);
        var handler = new StubHandler("""
            {
              "data": { "total": 1, "serverTime": "2026-09-03T08:30:00Z",
              "items": [
                {
                  "email": "person@outlook.com",
                  "domain": "outlook.com",
                  "credential": "new-refresh-token",
                  "client_id": "new-client"
                }
              ] }
            }
            """);
        var cursor = new FakeCursorStore();
        var service = new UpstreamMailboxSyncService(
            new StubHttpClientFactory(handler),
            fixture.Intake,
            cursor,
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true,
                Endpoint = "https://platform.example/api/mailboxes",
                BearerToken = "platform-token"
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var result = await service.PullAsync(fixture.UserId);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal("https://platform.example/api/mailboxes", handler.RequestUri?.GetLeftPart(UriPartial.Path));
        Assert.Contains("page=1", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("pageSize=1000", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("platform-token", handler.AuthorizationParameter);
        Assert.Equal("server-config", handler.InstallationId);
        Assert.False(string.IsNullOrWhiteSpace(handler.DeviceName));
        Assert.False(string.IsNullOrWhiteSpace(handler.OperatingSystem));
        Assert.False(string.IsNullOrWhiteSpace(handler.AppVersion));
        var account = Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Equal("outlook.com", account.ImportedDomain);
        Assert.Equal("enc:new-refresh-token", account.Password);
        Assert.Equal("new-refresh-token", account.OAuthRefreshToken);
        Assert.Equal("new-client", account.ClientId);
        Assert.True(account.IsEnabled);

        account.PreferredIncomingAuth = MailAuthenticationMethod.OAuth2;
        account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        account.OAuthRefreshToken = "rotated-by-provider";
        await fixture.Context.SaveChangesAsync();
        await service.PullAsync(fixture.UserId);
        Assert.Contains("updatedSince=1788424200", handler.RequestUri?.Query, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        account = await fixture.Context.MailAccounts.SingleAsync();
        Assert.Equal(MailAuthenticationMethod.OAuth2, account.PreferredIncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
        Assert.Equal("rotated-by-provider", account.OAuthRefreshToken);
    }

    [Fact]
    public async Task PullAsync_reads_pages_serially_and_persists_the_first_page_timestamp()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new PagingHandler();
        var cursor = new FakeCursorStore();
        var service = new UpstreamMailboxSyncService(
            new StubHttpClientFactory(handler),
            fixture.Intake,
            cursor,
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true,
                Endpoint = "https://platform.example/api/external/account-credentials",
                BearerToken = "platform-token",
                PageSize = 1
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var result = await service.PullAsync(fixture.UserId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("page=2", handler.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal("123", cursor.Cursor);
        Assert.Equal(2, await fixture.Context.MailAccounts.CountAsync());
    }

    [Fact]
    public async Task Missing_bearer_secret_fails_closed_before_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StubHandler("{}");
        var service = new UpstreamMailboxSyncService(new StubHttpClientFactory(handler), fixture.Intake,
            new FakeCursorStore(),
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true,
                Endpoint = "https://platform.example/api/external/account-credentials",
                RequireBearerToken = true
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var result = await service.PullAsync(fixture.UserId);

        Assert.False(result.Succeeded);
        Assert.Contains("缺少平台租户 Token", result.Error);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task PullAsync_does_not_try_platform_login_after_token_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new RetryHandler();
        var service = new UpstreamMailboxSyncService(
            new StubHttpClientFactory(handler),
            fixture.Intake,
            new FakeCursorStore(),
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true,
                Endpoint = "https://platform.example/api/external/account-credentials",
                BearerToken = "bundled-token",
                RequireBearerToken = true
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var result = await service.PullAsync(fixture.UserId);

        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 401", result.Error);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("bundled-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task Priority_email_pull_bypasses_running_full_pull_and_preserves_its_cursor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new ConcurrentPullHandler();
        var cursor = new FakeCursorStore();
        var service = new UpstreamMailboxSyncService(
            new StubHttpClientFactory(handler), fixture.Intake, cursor,
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true,
                Endpoint = "https://platform.example/api/external/account-credentials",
                BearerToken = "platform-token"
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var fullPull = service.PullAsync(fixture.UserId);
        await handler.FullPullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var priority = await service.PullEmailAsync(fixture.UserId, "person@yahoo.com")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(priority.Succeeded);
        Assert.Equal(1, priority.Created);
        Assert.Null(cursor.Cursor);
        Assert.Equal("person@yahoo.com", System.Web.HttpUtility.ParseQueryString(handler.PriorityRequest!.Query)["email"]);
        Assert.Null(System.Web.HttpUtility.ParseQueryString(handler.PriorityRequest.Query)["updatedSince"]);
        Assert.Equal("1", System.Web.HttpUtility.ParseQueryString(handler.PriorityRequest.Query)["pageSize"]);

        handler.CompleteFullPull();
        Assert.True((await fullPull).Succeeded);
        Assert.Equal("123", cursor.Cursor);
        var account = Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Equal("enc:new-code", account.Password);
    }

    [Fact]
    public async Task Priority_email_pull_rejects_a_different_email_in_platform_response()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StubHandler("""
            {"data":{"total":1,"items":[{"email":"other@yahoo.com","credential":"secret"}]}}
            """);
        var service = new UpstreamMailboxSyncService(
            new StubHttpClientFactory(handler), fixture.Intake, new FakeCursorStore(),
            Options.Create(new UpstreamMailboxSyncOptions
            {
                Enabled = true, Endpoint = "https://platform.example/mailboxes", BearerToken = "platform-token"
            }),
            NullLogger<UpstreamMailboxSyncService>.Instance);

        var result = await service.PullEmailAsync(fixture.UserId, "person@yahoo.com");

        Assert.False(result.Succeeded);
        Assert.Empty(await fixture.Context.MailAccounts.ToListAsync());
    }

    private sealed class ConcurrentPullHandler : HttpMessageHandler
    {
        public TaskCompletionSource FullPullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeFullPull = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? PriorityRequest { get; private set; }

        public void CompleteFullPull() => _completeFullPull.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var email = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["email"];
            if (email is null)
            {
                FullPullStarted.TrySetResult();
                await _completeFullPull.Task.WaitAsync(cancellationToken);
                return Json("{\"data\":{\"total\":1,\"serverTimestamp\":123,\"items\":[{\"email\":\"person@yahoo.com\",\"credential\":\"stale-code\"}]}}");
            }

            PriorityRequest = request.RequestUri;
            return Json("{\"data\":{\"total\":1,\"items\":[{\"email\":\"person@yahoo.com\",\"credential\":\"new-code\"}]}}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? InstallationId { get; private set; }
        public string? DeviceName { get; private set; }
        public string? OperatingSystem { get; private set; }
        public string? AppVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            InstallationId = ReadHeader(request, "X-MailAssistant-Installation-Id");
            DeviceName = ReadHeader(request, "X-MailAssistant-Device-Name");
            OperatingSystem = ReadHeader(request, "X-MailAssistant-OS");
            AppVersion = ReadHeader(request, "X-MailAssistant-App-Version");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }

        private static string? ReadHeader(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PagingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var page = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"];
            var body = page == "1"
                ? "{\"data\":{\"total\":2,\"page\":1,\"pageSize\":1,\"hasMore\":true,\"serverTimestamp\":123,\"items\":[{\"email\":\"one@yahoo.com\",\"credential\":\"one-code\"}]}}"
                : "{\"data\":{\"total\":2,\"page\":2,\"pageSize\":1,\"hasMore\":false,\"serverTimestamp\":999,\"items\":[{\"email\":\"two@yahoo.com\",\"credential\":\"two-code\"}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastAuthorization = request.Headers.Authorization?.Parameter;
            if (RequestCount == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"expired\"}", Encoding.UTF8, "application/json")
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":{\"total\":0,\"serverTime\":\"2026-09-03T08:30:00Z\",\"items\":[]}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class FakeCursorStore : IUpstreamMailboxSyncCursorStore
    {
        public string? Cursor { get; private set; }
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Cursor);
        public Task WriteAsync(string cursor, CancellationToken cancellationToken = default)
        {
            Cursor = cursor;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            Cursor = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MailArchiverDbContext Context { get; }
        public MailCredentialIntakeService Intake { get; }
        public int UserId { get; }

        private Fixture(
            SqliteConnection connection,
            MailArchiverDbContext context,
            MailCredentialIntakeService intake,
            int userId)
        {
            _connection = connection;
            Context = context;
            Intake = intake;
            UserId = userId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new MailArchiverDbContext(
                new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var user = new User { Username = "test", Email = "test@example.com", IsSelfManager = true };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var encryption = new FakeEncryption();
            var registry = new MailProviderRegistry([
                new GmailMailProviderModule(null!, encryption),
                new YahooMailProviderModule(null!, encryption),
                new GmxMailProviderModule(null!, encryption),
                new OutlookMailProviderModule(null!, null!, null, encryption),
                new CustomDomainMailProviderModule(null!, encryption)
            ]);
            var intake = new MailCredentialIntakeService(context, registry, encryption, new FakeIntakeVerifier());
            return new Fixture(connection, context, intake, user.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeEncryption : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue[4..];
    }
}
