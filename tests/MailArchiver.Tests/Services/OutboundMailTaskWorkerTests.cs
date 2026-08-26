using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public class OutboundMailTaskWorkerTests
{
    [Fact]
    public async Task ProcessNextDueItem_sends_only_due_item_and_persists_confirmation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailArchiverDbContext>(options => options.UseSqlite(connection));
        var outbound = new FakeOutboundMailService();
        services.AddSingleton<IOutboundMailService>(outbound);
        await using var provider = services.BuildServiceProvider();

        int dueId;
        int futureId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            await context.Database.EnsureCreatedAsync();
            var user = CreateUser();
            var account = CreateAccount();
            var task = new OutboundMailTask
            {
                Name = "test",
                CreatedByUser = user,
                Items =
                [
                    CreateItem(account, DateTime.UtcNow.AddMinutes(-1), "due"),
                    CreateItem(account, DateTime.UtcNow.AddHours(1), "future")
                ]
            };
            context.Add(task);
            await context.SaveChangesAsync();
            dueId = task.Items.Single(item => item.Subject == "due").Id;
            futureId = task.Items.Single(item => item.Subject == "future").Id;
        }

        var worker = new OutboundMailTaskWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboundMailOptions { Enabled = true }),
            NullLogger<OutboundMailTaskWorker>.Instance);

        Assert.True(await worker.ProcessNextDueItemAsync(CancellationToken.None));
        Assert.Single(outbound.Messages);
        Assert.Equal("due", outbound.Messages[0].Subject);

        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
        var due = await verify.OutboundMailTaskItems.FindAsync(dueId);
        var future = await verify.OutboundMailTaskItems.FindAsync(futureId);
        Assert.Equal(OutboundMailTaskItemStatus.Sent, due!.Status);
        Assert.Equal("test-message-id", due.MessageId);
        Assert.Equal(OutboundMailTaskItemStatus.Pending, future!.Status);
    }

    [Fact]
    public async Task ProcessNextDueItem_marks_a_confirmed_failure_as_failed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailArchiverDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IOutboundMailService>(new FakeOutboundMailService { Failure = new InvalidOperationException() });
        await using var provider = services.BuildServiceProvider();

        int itemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            await context.Database.EnsureCreatedAsync();
            var task = new OutboundMailTask
            {
                Name = "test",
                CreatedByUser = CreateUser(),
                Items = [CreateItem(CreateAccount(), DateTime.UtcNow.AddMinutes(-1), "failure")]
            };
            context.Add(task);
            await context.SaveChangesAsync();
            itemId = task.Items.Single().Id;
        }

        var worker = new OutboundMailTaskWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboundMailOptions { Enabled = true }),
            NullLogger<OutboundMailTaskWorker>.Instance);
        Assert.True(await worker.ProcessNextDueItemAsync(CancellationToken.None));

        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
        var item = await verify.OutboundMailTaskItems.FindAsync(itemId);
        Assert.Equal(OutboundMailTaskItemStatus.Failed, item!.Status);
        Assert.Null(item.MessageId);
        Assert.NotEmpty(item.ErrorMessage);
    }

    private static User CreateUser() => new()
    {
        Username = "user",
        Email = "user@local",
        IsAdmin = true
    };

    private static MailAccount CreateAccount() => new()
    {
        Name = "Sender",
        EmailAddress = "sender@outlook.com",
        Username = "sender@outlook.com",
        Provider = ProviderType.MSA,
        OAuthRefreshToken = "refresh-token",
        IsEnabled = true
    };

    private static OutboundMailTaskItem CreateItem(MailAccount account, DateTime scheduledAt, string subject) => new()
    {
        MailAccount = account,
        CsvRowNumber = 2,
        ScheduledAtUtc = scheduledAt,
        Recipient = "target@gmx.com",
        Subject = subject,
        Body = "body"
    };

    private sealed class FakeOutboundMailService : IOutboundMailService
    {
        public List<OutboundMailMessage> Messages { get; } = [];
        public Exception? Failure { get; init; }

        public Task<OutboundMailResult> SendAsync(
            MailAccount account,
            OutboundMailMessage message,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
                throw Failure;
            Messages.Add(message);
            return Task.FromResult(new OutboundMailResult(true, "test-message-id"));
        }
    }
}
