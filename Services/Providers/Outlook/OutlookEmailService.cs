using MailArchiver.Data;
using MailArchiver.Models;
namespace MailArchiver.Services.Providers.Outlook;

/// <summary>One receiving decision for sync, validation and folder listing. Restore remains read-only.</summary>
public sealed class OutlookEmailService(ImapEmailService imap, OutlookGraphMailSyncService graph,
    IOutlookGraphMailboxClient reader, OutlookIncomingRoutePolicy routes, MailArchiverDbContext context,
    ISyncJobService jobs) : IProviderEmailService
{
    public async Task SyncMailAccountAsync(MailAccount account, string? jobId = null, MailSyncRequestOptions? options = null)
    {
        try
        {
            await routes.ExecuteAsync(account,
                async () => { await graph.SyncAsync(account, jobId, options); return true; },
                async () => { await imap.SyncMailAccountAsync(account, jobId, options); return true; });
        }
        catch (Exception ex)
        {
            var failure = MailConnectionFailurePolicy.Classify(ex);
            if (jobId is not null && jobs.GetJob(jobId)?.Status != SyncJobStatus.Cancelled)
            {
                if (failure.Code == MailFailureCode.RateLimited) jobs.CompleteJobRateLimited(jobId, failure.Message, failure.Code.ToString());
                else jobs.CompleteJob(jobId, false, failure.Message, failure.Code.ToString());
            }
            throw;
        }
    }
    public async Task<bool> TestConnectionAsync(MailAccount account)
    {
        try
        {
            return await routes.ExecuteAsync(account,
                async () => { await reader.VerifyAsync(account); account.CredentialScope = MailCredentialScope.Graph; account.PreferredIncomingAuth = MailAuthenticationMethod.OAuth2; return true; },
                async () => await imap.TestConnectionAsync(account) ? true : throw new MailKit.Security.AuthenticationException("Outlook IMAP 收件验证未通过。"));
        }
        catch { return false; }
    }
    public async Task<List<string>> GetMailFoldersAsync(int accountId)
    {
        var account = await context.MailAccounts.FindAsync(accountId) ?? throw new InvalidOperationException("邮箱不存在。");
        return await routes.ExecuteAsync(account,
            async () => (await reader.GetFoldersAsync(account)).Select(f => f.Path).ToList(),
            () => imap.GetMailFoldersAsync(accountId));
    }
    public Task<bool> RestoreEmailToFolderAsync(int emailId, int targetAccountId, string folderName)
        => imap.RestoreEmailToFolderAsync(emailId, targetAccountId, folderName);
    public Task<bool> RestoreEmailToFolderAsync(int emailId, int targetAccountId, string folderName, bool preserveFolderStructure)
        => imap.RestoreEmailToFolderAsync(emailId, targetAccountId, folderName, preserveFolderStructure);
    public Task<(int Successful, int Failed)> RestoreMultipleEmailsWithProgressAsync(List<int> emailIds, int targetAccountId,
        string folderName, Action<int, int, int> progressCallback, CancellationToken cancellationToken = default)
        => imap.RestoreMultipleEmailsWithProgressAsync(emailIds, targetAccountId, folderName, progressCallback, cancellationToken);
    public Task<(int Successful, int Failed)> RestoreMultipleEmailsWithProgressAsync(List<int> emailIds, int targetAccountId,
        string folderName, bool preserveFolderStructure, Action<int, int, int> progressCallback, CancellationToken cancellationToken = default)
        => imap.RestoreMultipleEmailsWithProgressAsync(emailIds, targetAccountId, folderName, preserveFolderStructure, progressCallback, cancellationToken);
    public async Task<bool> ResyncAccountAsync(int accountId)
    {
        var account = await context.MailAccounts.FindAsync(accountId);
        if (account is null) return false;
        await SyncMailAccountAsync(account); return true;
    }
    public Task<int> GetEmailCountByAccountAsync(int accountId) => imap.GetEmailCountByAccountAsync(accountId);
}
