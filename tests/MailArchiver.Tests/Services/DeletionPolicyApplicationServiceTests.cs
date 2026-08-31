using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Integration tests for <see cref="DeletionPolicyApplicationService"/>.
/// The service applies a 10-second startup delay and runs as a background service,
/// so tests must start it, poll for the expected change, then stop it cleanly.
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class DeletionPolicyApplicationServiceTests
{
    private readonly TestDbFixture _fixture;
    public DeletionPolicyApplicationServiceTests(TestDbFixture fixture) => _fixture = fixture;

    private DeletionPolicyApplicationService CreateService(MailArchiverDbContext sharedContext, bool deletionAllowed)
    {
        var options = Options.Create(new DeletionPolicyOptions { DeletionAllowed = deletionAllowed });
        var services = new ServiceCollection();
        services.AddSingleton(sharedContext);
        var sp = services.BuildServiceProvider();
        return new DeletionPolicyApplicationService(
            NullLogger<DeletionPolicyApplicationService>.Instance,
            sp.GetRequiredService<IServiceScopeFactory>(),
            options);
    }

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var account = new MailAccount
        {
            Name = $"del-{Guid.NewGuid():N}".Substring(0, 25),
            EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP,
            IsEnabled = true,
            LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        return account;
    }

    private static ArchivedEmail BuildEmail(MailAccount acct, bool isLocked) => new()
    {
        MailAccountId = acct.Id,
        MessageId = Guid.NewGuid().ToString(),
        Subject = "locked-test",
        From = "a@x.com",
        To = "b@x.com",
        Cc = string.Empty,
        Bcc = string.Empty,
        Body = "x",
        HtmlBody = string.Empty,
        SentDate = DateTime.UtcNow.AddDays(-1),
        ReceivedDate = DateTime.UtcNow,
        IsOutgoing = false,
        HasAttachments = false,
        FolderName = "INBOX",
        IsLocked = isLocked
    };

    /// <summary>
    /// Starts the background service, polls until the expected IsLocked state is reached,
    /// then stops the service cleanly. Returns the final IsLocked value.
    /// </summary>
    private async Task<bool> RunAndWaitForLockStateAsync(MailArchiverDbContext ctx, int emailId,
        bool deletionAllowed, bool expectedLocked, TimeSpan timeout)
    {
        var svc = CreateService(ctx, deletionAllowed);
        using var cts = new CancellationTokenSource(timeout);
        await svc.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                ctx.ChangeTracker.Clear();
                var current = await ctx.ArchivedEmails.AsNoTracking().FirstAsync(e => e.Id == emailId);
                if (current.IsLocked == expectedLocked) break;
                await Task.Delay(500);
            }
        }
        catch { }

        // Stop the background service cleanly (cancels the token and waits for ExecuteAsync to exit).
        cts.Cancel();
        try { await svc.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }

        ctx.ChangeTracker.Clear();
        var final = await ctx.ArchivedEmails.AsNoTracking().FirstAsync(e => e.Id == emailId);
        return final.IsLocked;
    }

    [Fact]
    public async Task ExecuteAsync_DeletionAllowedTrue_UnlocksMultipleEmailsInSameBatch()
    {
        var ctx = _fixture.CreateContext();
        try
        {
            var acct = await SeedAccountAsync(ctx);
            var emails = new[]
            {
                BuildEmail(acct, isLocked: true),
                BuildEmail(acct, isLocked: true)
            };
            ctx.ArchivedEmails.AddRange(emails);
            await ctx.SaveChangesAsync();

            var finalLocked = await RunAndWaitForLockStateAsync(ctx, emails[0].Id,
                deletionAllowed: true, expectedLocked: false, timeout: TimeSpan.FromSeconds(30));
            Assert.False(finalLocked);

            ctx.ChangeTracker.Clear();
            var lockStates = await ctx.ArchivedEmails.AsNoTracking()
                .Where(email => emails.Select(candidate => candidate.Id).Contains(email.Id))
                .Select(email => email.IsLocked)
                .ToListAsync();
            Assert.Equal(2, lockStates.Count);
            Assert.All(lockStates, Assert.False);
        }
        finally
        {
            await CleanupTestAccountAsync(ctx);
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_LocalSqlite_UnlocksMultipleEmailsInSameBatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seedContext = new MailArchiverDbContext(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            var account = await SeedAccountAsync(seedContext);
            seedContext.ArchivedEmails.AddRange(
                BuildEmail(account, isLocked: true),
                BuildEmail(account, isLocked: true));
            await seedContext.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<MailArchiverDbContext>(builder => builder.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var service = new DeletionPolicyApplicationService(
            NullLogger<DeletionPolicyApplicationService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeletionPolicyOptions { DeletionAllowed = true }));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(12));
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        await using var verifyContext = new MailArchiverDbContext(options);
        var lockStates = await verifyContext.ArchivedEmails.AsNoTracking()
            .Select(email => email.IsLocked)
            .ToListAsync();
        Assert.Equal(2, lockStates.Count);
        Assert.All(lockStates, Assert.False);
    }

    [Fact]
    public async Task ExecuteAsync_DeletionAllowedFalse_SetsIsLockedTrue()
    {
        var ctx = _fixture.CreateContext();
        try
        {
            var acct = await SeedAccountAsync(ctx);
            var email = BuildEmail(acct, isLocked: false);
            ctx.ArchivedEmails.Add(email);
            await ctx.SaveChangesAsync();
            var emailId = email.Id;

            var finalLocked = await RunAndWaitForLockStateAsync(ctx, emailId,
                deletionAllowed: false, expectedLocked: true, timeout: TimeSpan.FromSeconds(30));
            Assert.True(finalLocked);
        }
        finally
        {
            await CleanupTestAccountAsync(ctx);
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCorrect_NoChange()
    {
        var ctx = _fixture.CreateContext();
        try
        {
            var acct = await SeedAccountAsync(ctx);
            // The Dev-DB enforces IsLocked=true on insert (DEFAULT true + compliance trigger),
            // so we can only seed locked emails. With deletionAllowed=false the target is
            // IsLocked=true, meaning the email is already correct and the service should not
            // change it. We verify the service runs and the value stays true.
            var email = BuildEmail(acct, isLocked: true);
            ctx.ArchivedEmails.Add(email);
            await ctx.SaveChangesAsync();
            var emailId = email.Id;

            var finalLocked = await RunAndWaitForLockStateAsync(ctx, emailId,
                deletionAllowed: false, expectedLocked: true, timeout: TimeSpan.FromSeconds(20));
            Assert.True(finalLocked);
        }
        finally
        {
            await CleanupTestAccountAsync(ctx);
            await ctx.DisposeAsync();
        }
    }

    /// <summary>
    /// Removes all test rows for accounts created in the given context.
    /// </summary>
    private static async Task CleanupTestAccountAsync(MailArchiverDbContext ctx)
    {
        var accountIds = await ctx.MailAccounts.AsNoTracking()
            .Where(a => a.EmailAddress.EndsWith("@test.local"))
            .Select(a => a.Id)
            .ToListAsync();

        if (accountIds.Count == 0) return;

        var emails = await ctx.ArchivedEmails.Where(e => accountIds.Contains(e.MailAccountId)).ToListAsync();
        ctx.ArchivedEmails.RemoveRange(emails);

        var caches = await ctx.AccountStorageCaches.Where(c => accountIds.Contains(c.MailAccountId)).ToListAsync();
        ctx.AccountStorageCaches.RemoveRange(caches);

        var states = await ctx.AccountStorageBackfillStates.Where(s => accountIds.Contains(s.MailAccountId)).ToListAsync();
        ctx.AccountStorageBackfillStates.RemoveRange(states);

        var umas = await ctx.UserMailAccounts.Where(uma => accountIds.Contains(uma.MailAccountId)).ToListAsync();
        ctx.UserMailAccounts.RemoveRange(umas);

        var accts = await ctx.MailAccounts.Where(a => accountIds.Contains(a.Id)).ToListAsync();
        ctx.MailAccounts.RemoveRange(accts);

        try { await ctx.SaveChangesAsync(); }
        catch { /* best-effort cleanup */ }
    }
}
