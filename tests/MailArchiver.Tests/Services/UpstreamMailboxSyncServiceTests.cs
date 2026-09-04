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
    public async Task Rejected_upstream_rows_stop_mail_sync_but_keep_successful_intake_results()
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
            new FakeConnectionStore(),
            cursor,
            Options.Create(new UpstreamMailboxSyncOptions { Enabled = true, Endpoint = "https://platform.example/mailboxes", BearerToken = "test-token" }),
            NullLogger<UpstreamMailboxSyncService>.Instance);
        var result = await service.PullAsync(fixture.UserId);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Rejected);
        Assert.Contains("同步已停止", result.Error);
        Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Null(cursor.Cursor);
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
            new FakeConnectionStore(),
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
        Assert.Equal("https://platform.example/api/mailboxes", handler.RequestUri?.ToString());
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
        Assert.Contains("updatedSince=2026-09-03T08%3a30%3a00Z", handler.RequestUri?.Query, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        account = await fixture.Context.MailAccounts.SingleAsync();
        Assert.Equal(MailAuthenticationMethod.OAuth2, account.PreferredIncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
        Assert.Equal("rotated-by-provider", account.OAuthRefreshToken);
    }

    [Fact]
    public async Task Missing_bearer_secret_fails_closed_before_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StubHandler("{}");
        var service = new UpstreamMailboxSyncService(new StubHttpClientFactory(handler), fixture.Intake,
            new FakeConnectionStore(),
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
        Assert.Contains("密钥未配置", result.Error);
        Assert.Null(handler.RequestUri);
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
            InstallationId = ReadHeader(request, "X-Kouzi-Installation-Id");
            DeviceName = ReadHeader(request, "X-Kouzi-Device-Name");
            OperatingSystem = ReadHeader(request, "X-Kouzi-OS");
            AppVersion = ReadHeader(request, "X-Kouzi-App-Version");
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

    private sealed class FakeConnectionStore : IUpstreamMailboxConnectionStore
    {
        public Task<UpstreamMailboxConnection?> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<UpstreamMailboxConnection?>(null);

        public Task<UpstreamMailboxConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsync(string endpoint, string bearerToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
