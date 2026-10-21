using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public sealed class CsvImportPipelineTests
{
    [Fact]
    public async Task Offline_import_saves_mixed_domains_and_later_network_verification_failure_does_not_undo_import()
    {
        await using var fixture = await Fixture.CreateAsync();
        var domains = new[] { "gmx.es", "gmx.fr", "brand-new.example", "sub.company.example", "gmail.com", "yahoo.com", "outlook.com" };
        var job = fixture.Job(domains);
        fixture.Worker.QueueImport(job);
        await fixture.WaitForAsync(job, CsvImportJobStatus.AwaitingVerification);
        Assert.Equal(domains.Length, job.CreatedCount);
        Assert.Equal(0, job.FailedCount);
        Assert.Equal(0, fixture.Verifier.Calls);
        Assert.Equal(domains.Length, await fixture.CountAccountsAsync());
        Assert.True(fixture.Worker.StartVerification(job.JobId));
        await fixture.WaitForAsync(job, CsvImportJobStatus.CompletedWithErrors);
        Assert.Equal(domains.Length, job.VerificationNetworkFailureCount);
        Assert.Equal(0, job.FailedCount);
        Assert.Equal(domains.Length, await fixture.CountAccountsAsync());
        Assert.All(job.FailedSamples, r => Assert.DoesNotContain("opaque-secret", r.Reason));
    }

    [Fact]
    public async Task Gmail_oauth_refresh_token_is_not_rejected_by_the_app_password_length_rule()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = fixture.Job(["gmail.com"]);
        job.Rows[0].Password = "long-google-oauth-refresh-token-without-sixteen-character-limit";
        job.Rows[0].ClientId = "google-client";
        fixture.Worker.QueueImport(job);
        await fixture.WaitForAsync(job, CsvImportJobStatus.AwaitingVerification);
        Assert.Equal(0, job.FormatWarningCount);
        Assert.True(fixture.Worker.StartVerification(job.JobId));
        await fixture.WaitForAsync(job, CsvImportJobStatus.CompletedWithErrors);
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(0, job.VerificationFormatFailureCount);
        Assert.Equal(1, job.VerificationNetworkFailureCount);
    }

    [Fact]
    public async Task Retry_only_rechecks_failed_accounts_and_never_reimports_them()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = fixture.Job(["works.example", "fails.example"]);
        fixture.Verifier.SuccessDomain = "works.example";
        fixture.Worker.QueueImport(job);
        await fixture.WaitForAsync(job, CsvImportJobStatus.AwaitingVerification);
        fixture.Worker.StartVerification(job.JobId);
        await fixture.WaitForAsync(job, CsvImportJobStatus.CompletedWithErrors);
        Assert.Equal(1, job.VerificationSuccessCount);
        fixture.Verifier.SuccessDomain = "fails.example";
        Assert.True(fixture.Worker.RetryVerification(job.JobId));
        await fixture.WaitForAsync(job, CsvImportJobStatus.Completed);
        Assert.Equal(1, job.VerificationTotalCount);
        Assert.Equal(1, job.VerificationSuccessCount);
        Assert.Equal(3, fixture.Verifier.Calls);
        Assert.Equal(2, await fixture.CountAccountsAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _services;
        public CsvImportService Worker { get; }
        public OfflineVerifier Verifier { get; }
        private readonly int _userId;
        private Fixture(string directory, ServiceProvider services, CsvImportService worker, OfflineVerifier verifier, int userId)
        { _directory = directory; _services = services; Worker = worker; Verifier = verifier; _userId = userId; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "csv-pipeline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var services = new ServiceCollection();
            services.AddDbContext<MailArchiverDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(root, "test.db")}"));
            var verifier = new OfflineVerifier();
            services.AddSingleton<IMailCredentialVerifier>(verifier);
            services.AddSingleton<ICredentialEncryptionService, Encryption>();
            services.AddScoped<ISyncFailureRecorder, SyncFailureRecorder>();
            services.AddScoped<IMailProviderRegistry>(p =>
            {
                var encryption = p.GetRequiredService<ICredentialEncryptionService>();
                return new MailProviderRegistry([
                    new GmailMailProviderModule(null!, encryption), new YahooMailProviderModule(null!, encryption),
                    new GmxMailProviderModule(null!, encryption), new OutlookMailProviderModule(null!, null!, credentialEncryption: encryption),
                    new CustomDomainMailProviderModule(null!, encryption)]);
            });
            services.AddScoped<MailCredentialIntakeService>();
            var provider = services.BuildServiceProvider();
            int userId;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                await db.Database.EnsureCreatedAsync();
                var user = new User { Username = "pipeline", Email = "pipeline@example.com" };
                db.Users.Add(user);
                await db.SaveChangesAsync();
                userId = user.Id;
            }
            var options = Options.Create(new MailConcurrencyOptions());
            var worker = new CsvImportService(provider, NullLogger<CsvImportService>.Instance,
                new MailConcurrencyCoordinator(options), options);
            await worker.StartAsync(CancellationToken.None);
            return new(root, provider, worker, verifier, userId);
        }

        public CsvImportJob Job(string[] domains)
        {
            var job = new CsvImportJob { UserId = _userId, TotalRows = domains.Length, Enabled = false };
            job.Rows.AddRange(domains.Select((domain, i) => new CsvParsedRow { Email = $"reader@{domain}", Domain = domain,
                Password = domain == "gmail.com" ? "abcdefghijklmnop" : "opaque-secret", LineNumber = i + 2 }));
            return job;
        }
        public async Task<int> CountAccountsAsync()
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>().MailAccounts.CountAsync();
        }
        public async Task WaitForAsync(CsvImportJob job, CsvImportJobStatus status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (job.Status != status)
            {
                Assert.NotEqual(CsvImportJobStatus.Failed, job.Status);
                await Task.Delay(10, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
            await _services.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }
    private sealed class OfflineVerifier : IMailCredentialVerifier
    {
        private int _calls;
        public int Calls => _calls;
        public string? SuccessDomain { get; set; }
        public Task VerifyAsync(MailAccount account, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (account.EmailAddress.EndsWith("@" + SuccessDomain, StringComparison.Ordinal))
            {
                account.CredentialDetectionStatus = "IncomingVerified";
                return Task.CompletedTask;
            }
            throw new HttpRequestException("offline opaque-secret");
        }
    }
    private sealed class Encryption : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue[4..];
    }
}
