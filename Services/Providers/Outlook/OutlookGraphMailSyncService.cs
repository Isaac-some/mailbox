using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Services.Providers.Graph;
using Microsoft.Graph.Models;
namespace MailArchiver.Services.Providers.Outlook;

public sealed class OutlookGraphMailSyncService(IOutlookGraphMailboxClient reader, GraphMailArchiver archiver,
    MailArchiverDbContext context, ISyncJobService jobs, EmailCoreService core)
{
    private static readonly (MailboxFolderCategory Category, string Id, string Path)[] StandardFolders =
    [
        (MailboxFolderCategory.Inbox, "inbox", "INBOX"), (MailboxFolderCategory.Junk, "junkemail", "Junk"),
        (MailboxFolderCategory.Sent, "sentitems", "Sent"), (MailboxFolderCategory.Drafts, "drafts", "Drafts"),
        (MailboxFolderCategory.Trash, "deleteditems", "Trash"), (MailboxFolderCategory.Archive, "archive", "Archive")
    ];
    public async Task SyncAsync(MailAccount account, string? jobId, MailSyncRequestOptions? requested)
    {
        var options = (requested ?? new(account.MailboxSyncLookbackDays)).Normalize();
        using var cancellation = new CancellationTokenSource();
        if (jobId is not null && jobs.GetJob(jobId) is { } job) job.CancellationTokenSource = cancellation;
        var token = cancellation.Token;
        var newEmails = 0; var processed = 0; var folderCount = 0;
        void CheckCancellation()
        {
            if (jobId is not null && jobs.GetJob(jobId)?.Status == SyncJobStatus.Cancelled)
                throw new OperationCanceledException("收件任务已取消。");
        }
        async Task ArchiveFolder(string id, string path, MailboxFolderCategory category, bool optional)
        {
            CheckCancellation();
            var limit = options.TargetCategory == category && options.TargetLimit.HasValue ? options.TargetLimit.Value
                : account.IsMailboxCategoryExpanded(category) ? 30 : 10;
            List<Message> messages;
            try { messages = await reader.ReadAsync(account, id, options.LookbackDays, limit, token); }
            catch (OutlookGraphReadException ex) when (optional && ex.StatusCode == 404) { return; }
            using var graph = reader.CreateClient(account);
            foreach (var message in messages)
            {
                CheckCancellation();
                var messageId = GraphMailArchiver.ResolveMessageId(message);
                if (!await archiver.IsDuplicateAsync(account.Id, messageId, message, path))
                {
                    if (!await archiver.ArchiveGraphEmailAsync(graph, account, message,
                        category == MailboxFolderCategory.Sent, path, skipDuplicateCheck: true))
                        throw new InvalidOperationException("邮件已获取，但本地保存未完成，请重试。");
                    newEmails++;
                }
                processed++;
                if (jobId is not null) jobs.UpdateJobProgress(jobId, j => { j.NewEmails = newEmails; j.ProcessedEmails = processed; j.CurrentFolder = path; });
            }
            folderCount++;
            if (jobId is not null) jobs.UpdateJobProgress(jobId, j => j.ProcessedFolders = folderCount);
        }
        // Persist the visible folder before discovering other folders or doing
        // attachment/local retention work for the remainder of the mailbox.
        var priority = options.TargetCategory ?? options.PriorityCategory ?? MailboxFolderCategory.Inbox;
        async Task ArchiveOtherFolders()
        {
            foreach (var folder in (await reader.GetFoldersAsync(account, token)).Where(f => f.Category == MailboxFolderCategory.Other))
            {
                if (account.ExcludedFoldersList.Any(f => f.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))) continue;
                await ArchiveFolder(folder.Id, folder.Path, folder.Category, optional: true);
            }
        }
        if (priority == MailboxFolderCategory.Other) await ArchiveOtherFolders();
        foreach (var folder in StandardFolders.OrderBy(f => f.Category == priority ? 0 : 1))
        {
            if (options.TargetCategory.HasValue && folder.Category != options.TargetCategory.Value) continue;
            if (account.ExcludedFoldersList.Any(f => f.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))) continue;
            await ArchiveFolder(folder.Id, folder.Path, folder.Category, optional: folder.Category != MailboxFolderCategory.Inbox);
        }
        if (options.TargetCategory is null && priority != MailboxFolderCategory.Other) await ArchiveOtherFolders();
        CheckCancellation();
        var tracked = await context.MailAccounts.FindAsync(account.Id);
        if (tracked is not null) { tracked.LastSync = DateTime.UtcNow; await context.SaveChangesAsync(); }
        await core.EnforceMailboxCategoryLimitsAsync(tracked ?? account, options.LookbackDays);
        if (jobId is not null) jobs.CompleteJob(jobId, true);
    }
}
