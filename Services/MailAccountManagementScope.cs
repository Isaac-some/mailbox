namespace MailArchiver.Services;

/// <summary>
/// Defines the account set used by administrator-wide management operations.
/// Administrators operate on every account; other users remain restricted to
/// accounts explicitly assigned to them.
/// </summary>
public static class MailAccountManagementScope
{
    public static IQueryable<MailAccount> Apply(
        IQueryable<MailAccount> accounts,
        int? currentUserId,
        bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        if (isAdministrator)
            return accounts;

        return currentUserId.HasValue
            ? accounts.Where(account => account.UserMailAccounts.Any(ownership =>
                ownership.UserId == currentUserId.Value))
            : accounts.Where(_ => false);
    }
}
