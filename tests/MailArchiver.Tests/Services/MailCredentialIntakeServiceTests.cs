using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.Services;

public class MailCredentialIntakeServiceTests
{
    [Fact]
    public async Task Csv_intake_stores_pending_credential_without_calling_provider()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake(
                "person@gmail.com",
                "abcd efgh ijkl mnop",
                "gmail.com",
                null),
            enabled: true,
            verifyCredential: false);

        Assert.True(result.Created);
        Assert.Equal("enc:abcdefghijklmnop", result.Account.Password);
        Assert.Equal("PendingVerification", result.Status);
        Assert.Equal(MailCredentialKind.Unknown, result.Kind);
        Assert.Equal(MailCredentialScope.Unknown, result.Scope);
        Assert.Equal(MailAuthenticationMethod.Unknown, result.Account.PreferredIncomingAuth);
        Assert.Null(result.Account.CredentialLastCheckedAt);
        Assert.Equal(0, fixture.Verifier.Calls);
        Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
    }

    [Theory]
    [InlineData("person@outlook.com", "oauth-client", "oauth-client")]
    [InlineData("person@yahoo.com", "same-imap-code", null)]
    [InlineData("person@gmx.com", "same-imap-code", null)]
    public async Task Standard_export_uses_client_id_only_for_outlook(
        string email,
        string secondColumn,
        string? expectedClientId)
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake(email, "main-credential", "example.com", secondColumn),
            enabled: true,
            verifyCredential: false);

        Assert.Equal(expectedClientId, result.Account.ClientId);
        Assert.Equal("enc:main-credential", result.Account.Password);
    }

    [Fact]
    public async Task Intake_normalizes_copied_credentials_and_verifies_before_saving()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake(
                "person@gmail.com",
                "abcd efgh ijkl mnop",
                "gmail.com",
                null),
            enabled: true);

        Assert.True(result.Created);
        Assert.Equal("enc:abcdefghijklmnop", result.Account.Password);
        Assert.Equal("abcdefghijklmnop", result.Account.OAuthRefreshToken);
        Assert.Equal("gmail.com", result.Account.ImportedDomain);
        Assert.Equal(MailCredentialKind.ImapPassword, result.Account.CredentialKind);
        Assert.Equal(MailAuthenticationMethod.Password, result.Account.PreferredIncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Unknown, result.Account.PreferredOutgoingAuth);
        Assert.Equal("IncomingVerified", result.Status);
        Assert.NotNull(result.Account.CredentialLastCheckedAt);
        Assert.Equal(1970, result.Account.LastSync.Year);
        Assert.Equal(1, fixture.Verifier.Calls);
    }

    [Fact]
    public async Task Duplicate_email_fully_replaces_the_four_fields_and_resets_route_memory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake("person@outlook.com", "old", "old.example", "old-client"),
            enabled: true);
        first.Account.PreferredIncomingAuth = MailAuthenticationMethod.OAuth2;
        first.Account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        first.Account.OAuthAccessToken = "stale-access";
        await fixture.Context.SaveChangesAsync();

        var second = await fixture.Service.UpsertAsync(
            fixture.UserId,
            new MailCredentialIntake("PERSON@outlook.com", "new secret", null, "new-client"),
            enabled: false);

        Assert.False(second.Created);
        Assert.Single(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Equal("enc:newsecret", second.Account.Password);
        Assert.Equal("newsecret", second.Account.OAuthRefreshToken);
        Assert.Null(second.Account.ImportedDomain);
        Assert.Equal("new-client", second.Account.ClientId);
        Assert.Null(second.Account.OAuthAccessToken);
        Assert.Equal(MailAuthenticationMethod.Password, second.Account.PreferredIncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Unknown, second.Account.PreferredOutgoingAuth);
        Assert.False(second.Account.IsEnabled);
    }

    [Fact]
    public async Task Repeated_identical_intake_preserves_the_successful_route_and_rotated_refresh_token()
    {
        await using var fixture = await Fixture.CreateAsync();
        var input = new MailCredentialIntake("person@outlook.com", "original-token", "outlook.com", "client");
        var first = await fixture.Service.UpsertAsync(fixture.UserId, input, enabled: true);
        first.Account.PreferredIncomingAuth = MailAuthenticationMethod.OAuth2;
        first.Account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        first.Account.OAuthRefreshToken = "provider-rotated-token";
        first.Account.CredentialDetectionStatus = "Verified";
        fixture.Verifier.Method = MailAuthenticationMethod.OAuth2;
        await fixture.Context.SaveChangesAsync();

        await fixture.Service.UpsertAsync(fixture.UserId, input, enabled: true);
        fixture.Context.ChangeTracker.Clear();
        var stored = await fixture.Context.MailAccounts.SingleAsync();

        Assert.Equal(MailAuthenticationMethod.OAuth2, stored.PreferredIncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Password, stored.PreferredOutgoingAuth);
        Assert.Equal("provider-rotated-token", stored.OAuthRefreshToken);
        Assert.Equal("IncomingVerified", stored.CredentialDetectionStatus);
    }

    [Fact]
    public async Task Rejected_new_credential_never_creates_an_account_or_ownership_link()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Verifier.Reject = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpsertAsync(
            fixture.UserId, new MailCredentialIntake("person@yahoo.com", "bad-code"), true));
        await fixture.Context.SaveChangesAsync();
        Assert.Empty(await fixture.Context.MailAccounts.ToListAsync());
        Assert.Empty(await fixture.Context.UserMailAccounts.ToListAsync());
    }

    [Fact]
    public async Task Rejected_replacement_does_not_dirty_the_existing_row_even_after_later_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.UpsertAsync(fixture.UserId,
            new MailCredentialIntake("person@yahoo.com", "old-code", "old.example", "old-client"), true);
        first.Account.OAuthRefreshToken = "rotated-token";
        first.Account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        await fixture.Context.SaveChangesAsync();
        fixture.Verifier.Reject = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpsertAsync(
            fixture.UserId, new MailCredentialIntake("person@yahoo.com", "bad-code"), false));
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        var stored = await fixture.Context.MailAccounts.SingleAsync();
        Assert.Equal("enc:old-code", stored.Password);
        Assert.Equal("rotated-token", stored.OAuthRefreshToken);
        Assert.Equal("old.example", stored.ImportedDomain);
        Assert.Null(stored.ClientId);
        Assert.True(stored.IsEnabled);
        Assert.Equal(MailAuthenticationMethod.Password, stored.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task Whitespace_only_input_is_rejected_before_login()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpsertAsync(
            fixture.UserId, new MailCredentialIntake("person@gmail.com", " \t\u3000\u200B\uFEFF"), true));
        Assert.Equal(0, fixture.Verifier.Calls);
        Assert.Empty(await fixture.Context.MailAccounts.ToListAsync());
    }

    [Fact]
    public async Task Whitespace_only_changes_preserve_rotated_tokens_and_outgoing_memory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.UpsertAsync(fixture.UserId,
            new MailCredentialIntake("person@outlook.com", "original-token", "outlook.com", "client"), true);
        // Simulate the old release's unnormalized stored input.
        first.Account.Password = "enc:original -token";
        first.Account.OAuthRefreshToken = "rotated-token";
        first.Account.PreferredOutgoingAuth = MailAuthenticationMethod.OAuth2;
        await fixture.Context.SaveChangesAsync();
        await fixture.Service.UpsertAsync(fixture.UserId,
            new MailCredentialIntake("person@outlook.com", "ori\u3000ginal-\u200Btoken", "outlook.com", "client"), true);
        Assert.Equal("enc:original-token", first.Account.Password);
        Assert.Equal("rotated-token", first.Account.OAuthRefreshToken);
        Assert.Equal(MailAuthenticationMethod.OAuth2, first.Account.PreferredOutgoingAuth);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MailArchiverDbContext Context { get; }
        public MailCredentialIntakeService Service { get; }
        public int UserId { get; }
        public FakeIntakeVerifier Verifier { get; }

        private Fixture(
            SqliteConnection connection,
            MailArchiverDbContext context,
            MailCredentialIntakeService service,
            int userId,
            FakeIntakeVerifier verifier)
        {
            _connection = connection;
            Context = context;
            Service = service;
            UserId = userId;
            Verifier = verifier;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new MailArchiverDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var user = new User
            {
                Username = "test",
                Email = "test@example.com",
                IsSelfManager = true
            };
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
            var verifier = new FakeIntakeVerifier();
            var service = new MailCredentialIntakeService(context, registry, encryption, verifier);
            return new Fixture(connection, context, service, user.Id, verifier);
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

internal sealed class FakeIntakeVerifier : IMailCredentialVerifier
{
    public int Calls { get; private set; }
    public bool Reject { get; set; }
    public MailAuthenticationMethod Method { get; set; } = MailAuthenticationMethod.Password;

    public Task VerifyAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Assert.Equal(0, account.Id);
        if (Reject)
        {
            account.OAuthRefreshToken = "must-not-leak-into-existing-row";
            throw new InvalidOperationException("模拟授权失败");
        }
        // Retain remembered routes, like the real provider funnel.
        if (account.PreferredIncomingAuth == MailAuthenticationMethod.Unknown)
            account.PreferredIncomingAuth = Method;
        account.CredentialKind = MailCredentialKind.ImapPassword;
        account.CredentialScope = MailCredentialScope.Imap;
        account.CredentialDetectionStatus = "IncomingVerified";
        account.CredentialLastCheckedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
