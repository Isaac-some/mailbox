using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Tests.Services;

public class MailAccountDeletionServiceTests
{
    [Fact]
    public async Task DeleteOutboundTaskDependencies_allows_account_deletion_without_removing_other_accounts_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseSqlite(connection)
            .Options;

        int accountToDeleteId;
        int retainedAccountId;
        int mixedTaskId;
        int emptyTaskId;
        await using (var setup = new MailArchiverDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var user = new User
            {
                Username = "admin",
                Email = "admin@local",
                IsAdmin = true
            };
            var accountToDelete = CreateAccount("delete@outlook.com");
            var retainedAccount = CreateAccount("keep@outlook.com");
            var mixedTask = new OutboundMailTask
            {
                Name = "mixed",
                CreatedByUser = user,
                Items =
                [
                    CreateItem(accountToDelete, "delete item"),
                    CreateItem(retainedAccount, "keep item")
                ]
            };
            var emptyTask = new OutboundMailTask
            {
                Name = "target only",
                CreatedByUser = user,
                Items = [CreateItem(accountToDelete, "only item")]
            };
            setup.AddRange(mixedTask, emptyTask);
            await setup.SaveChangesAsync();

            accountToDeleteId = accountToDelete.Id;
            retainedAccountId = retainedAccount.Id;
            mixedTaskId = mixedTask.Id;
            emptyTaskId = emptyTask.Id;
        }

        await using (var deletion = new MailArchiverDbContext(options))
        {
            await MailAccountDeletionService.DeleteOutboundTaskDependenciesAsync(
                deletion,
                accountToDeleteId,
                CancellationToken.None);
            var account = await deletion.MailAccounts.FindAsync(accountToDeleteId);
            deletion.MailAccounts.Remove(account!);
            await deletion.SaveChangesAsync();
        }

        await using var verify = new MailArchiverDbContext(options);
        Assert.Null(await verify.MailAccounts.FindAsync(accountToDeleteId));
        Assert.NotNull(await verify.MailAccounts.FindAsync(retainedAccountId));
        Assert.Null(await verify.OutboundMailTasks.FindAsync(emptyTaskId));

        var mixedItems = await verify.OutboundMailTaskItems
            .Where(item => item.OutboundMailTaskId == mixedTaskId)
            .ToListAsync();
        Assert.Single(mixedItems);
        Assert.Equal(retainedAccountId, mixedItems[0].MailAccountId);
    }

    private static MailAccount CreateAccount(string emailAddress) => new()
    {
        Name = emailAddress,
        EmailAddress = emailAddress,
        Username = emailAddress,
        Provider = ProviderType.MSA,
        IsEnabled = true
    };

    private static OutboundMailTaskItem CreateItem(MailAccount account, string subject) => new()
    {
        MailAccount = account,
        CsvRowNumber = 2,
        ScheduledAtUtc = DateTime.UtcNow,
        Recipient = "target@example.com",
        Subject = subject,
        Body = "body",
        Status = OutboundMailTaskItemStatus.Sent
    };
}
