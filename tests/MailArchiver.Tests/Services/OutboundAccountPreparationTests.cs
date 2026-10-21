using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Net.Imap;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailArchiver.Tests.Services;

public sealed class OutboundAccountPreparationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_local_csv_sender_is_verified_without_fetching_mail_even_if_platform_does_not_know_it(bool platformAvailable)
    {
        await using var f = await Fixture.Create();
        f.Upstream.Available = platformAvailable;
        await f.Service.PrepareAsync(f.Account, 7, default);
        Assert.Equal("SmtpVerified", f.Account.CredentialDetectionStatus);
        Assert.Equal(MailCredentialScope.Smtp, f.Account.CredentialScope);
        Assert.Equal(7, f.Upstream.Actor);
        Assert.Equal(f.Account.EmailAddress, f.Upstream.Email);
        Assert.Equal(1, f.Provider.Probes);
        Assert.Equal(0, f.Provider.Sends);
        Assert.Equal(0, f.Provider.IncomingLogins);
        Assert.Equal(1, f.Discovery.Calls);
        Assert.Equal("synthetic-password", f.Account.Password);
    }

    [Fact]
    public async Task Failed_smtp_probe_cannot_become_sendable_and_preserves_incoming_capability()
    {
        await using var f = await Fixture.Create();
        f.Account.CredentialScope = MailCredentialScope.Imap; await f.Db.SaveChangesAsync();
        f.Provider.Success = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PrepareAsync(f.Account, 7, default));
        Assert.Equal("ImapVerified", f.Account.CredentialDetectionStatus);
        Assert.Equal(MailCredentialScope.Imap, f.Account.CredentialScope);
        Assert.NotNull(f.Account.CredentialLastCheckedAt);
        Assert.Equal(0, f.Provider.Sends);
    }

    [Fact]
    public async Task Sending_reuses_recent_success_without_repeating_upstream_or_smtp_probe()
    {
        await using var f = await Fixture.Create();
        await f.Service.PrepareAsync(f.Account, 7, default);
        await f.Service.PrepareAsync(f.Account, 7, default, forceRefresh:false);
        Assert.Equal(1, f.Provider.Probes);
        Assert.Equal(1, f.Upstream.Calls);
        await f.Service.PrepareAsync(f.Account, 7, default);
        Assert.Equal(2, f.Provider.Probes);
    }

    [Fact]
    public async Task Disabled_account_is_rejected_before_platform_or_mail_network_access()
    {
        await using var f = await Fixture.Create();
        f.Account.IsEnabled = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PrepareAsync(f.Account, 7, default));
        Assert.Equal(0, f.Upstream.Calls);
        Assert.Equal(0, f.Provider.Probes);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        public MailArchiverDbContext Db = null!;
        public MailAccount Account = null!;
        public Upstream Upstream = new(); public Provider Provider = new(); public Discovery Discovery = new();
        public OutboundAccountPreparation Service => new(Db, Upstream, Discovery, Provider);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture { _connection = new("Data Source=:memory:") }; await f._connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(f._connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Account = new MailAccount { Name = "local", EmailAddress = "local@gmx.es", Password = "synthetic-password", CredentialDetectionStatus = "PendingVerification", MailProviderKind = MailProviderKind.Custom, LastSync = DateTime.UnixEpoch };
            f.Db.MailAccounts.Add(f.Account); await f.Db.SaveChangesAsync(); return f;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
    private sealed class Upstream : IUpstreamMailboxSyncService
    {
        public bool Available = true; public int Calls, Actor; public string? Email;
        public Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default) => throw new Exception("Must never pull whole library");
        public Task<UpstreamMailboxSyncResult> PullEmailAsync(int userId, string email, CancellationToken cancellationToken = default)
        { Calls++; Actor = userId; Email = email; return Task.FromResult(new UpstreamMailboxSyncResult(true, 0, 0, 0, Error:Available ? null : "Not found")); }
    }
    private sealed class Discovery : IMailEndpointDiscoveryService
    { public int Calls; public Task DiscoverAsync(MailAccount account, CancellationToken cancellationToken = default) { Calls++; return Task.CompletedTask; } }
    private sealed class Provider : IMailProviderModule, IMailProviderRegistry
    {
        public bool Success = true; public int Probes, Sends, IncomingLogins;
        public MailProviderKind Kind => MailProviderKind.Custom; public string DisplayName => "Test";
        public bool SupportsAddress(string emailAddress) => true; public void PrepareAccount(MailAccount account) {}
        public ImapEndpoint GetIncomingEndpoint(MailAccount account) => new("unused", 993, true);
        public MailAccountCapabilities Inspect(MailAccount account) => new(false, account.CredentialScope == MailCredentialScope.Smtp);
        public Task AuthenticateIncomingAsync(ImapClient client, MailAccount account, CancellationToken cancellationToken = default) { IncomingLogins++; return Task.CompletedTask; }
        public Task<bool> TestOutgoingConnectionAsync(MailAccount account, CancellationToken cancellationToken = default) { Probes++; return Task.FromResult(Success); }
        public Task<ProviderSendResult> SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken = default) { Sends++; return Task.FromResult(new ProviderSendResult(true)); }
        public IMailProviderModule For(MailProviderKind kind) => this; public IMailProviderModule For(MailAccount account) => this; public IMailProviderModule Detect(string emailAddress) => this;
    }
}
