using MailArchiver.Models;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailAccountManagementScopeTests
{
    [Fact]
    public void Administrator_scope_includes_all_accounts_not_only_the_20_owned_accounts()
    {
        var accounts = BuildAccounts(total: 55, ownedByCurrentUser: 20);

        var visible = MailAccountManagementScope.Apply(
            accounts.AsQueryable(), currentUserId: 1, isAdministrator: true).ToList();

        Assert.Equal(55, visible.Count);
    }

    [Fact]
    public void Non_administrator_scope_remains_limited_to_owned_accounts()
    {
        var accounts = BuildAccounts(total: 55, ownedByCurrentUser: 20);

        var visible = MailAccountManagementScope.Apply(
            accounts.AsQueryable(), currentUserId: 1, isAdministrator: false).ToList();

        Assert.Equal(20, visible.Count);
    }

    [Fact]
    public void Administrator_whitelist_deletion_plan_uses_all_accounts_not_the_visible_page()
    {
        var accounts = BuildAccounts(total: 55, ownedByCurrentUser: 20);
        var whitelist = Enumerable.Range(1, 5)
            .Select(id => $"account-{id}@example.com")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var accountsToDelete = MailAccountManagementScope.Apply(
                accounts.AsQueryable(), currentUserId: 1, isAdministrator: true)
            .Where(account => !whitelist.Contains(account.EmailAddress))
            .ToList();

        Assert.Equal(50, accountsToDelete.Count);
        Assert.Contains(accountsToDelete, account => account.Id == 55);
    }

    private static List<MailAccount> BuildAccounts(int total, int ownedByCurrentUser)
        => Enumerable.Range(1, total)
            .Select(id => new MailAccount
            {
                Id = id,
                Name = $"account-{id}",
                EmailAddress = $"account-{id}@example.com",
                UserMailAccounts = id <= ownedByCurrentUser
                    ? [new UserMailAccount { UserId = 1, MailAccountId = id }]
                    : [new UserMailAccount { UserId = 2, MailAccountId = id }]
            })
            .ToList();
}
