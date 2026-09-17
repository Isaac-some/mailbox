namespace MailArchiver.Models;

public sealed record MailSyncRequestOptions(
    int LookbackDays,
    MailboxFolderCategory? TargetCategory = null,
    int? TargetLimit = null)
{
    public MailSyncRequestOptions Normalize() => this with
    {
        LookbackDays = LookbackDays == 30 ? 30 : 7,
        TargetLimit = TargetCategory.HasValue && TargetLimit >= 30 ? 30 : null
    };

    public MailSyncRequestOptions Merge(MailSyncRequestOptions other)
    {
        var left = Normalize();
        var right = other.Normalize();
        var target = left.TargetCategory == right.TargetCategory
            ? left.TargetCategory
            : null;
        return new MailSyncRequestOptions(
            Math.Max(left.LookbackDays, right.LookbackDays),
            target,
            target.HasValue ? Math.Max(left.TargetLimit ?? 10, right.TargetLimit ?? 10) : null);
    }
}
