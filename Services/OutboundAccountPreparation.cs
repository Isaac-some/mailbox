using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.MailProviders;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace MailArchiver.Services;

/// <summary>Refresh and verify only a requested sender; never fetch its mailbox.</summary>
public sealed class OutboundAccountPreparation(
    MailArchiverDbContext context,
    IUpstreamMailboxSyncService upstream,
    IMailEndpointDiscoveryService discovery,
    IMailProviderRegistry providers)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public async Task PrepareAsync(MailAccount account, int userId, CancellationToken cancellationToken, bool forceRefresh = true)
    {
        var gate = Gates.GetOrAdd(context.Database.GetConnectionString() + ":" + account.Id, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await context.Entry(account).ReloadAsync(cancellationToken);
            if (!account.IsEnabled || account.Provider == ProviderType.IMPORT)
                throw new InvalidOperationException("该账号已停用或仅支持本地归档。");
            if (!forceRefresh && account.CredentialLastCheckedAt > DateTime.UtcNow.AddMinutes(-2)
                && account.CredentialScope is MailCredentialScope.Smtp or MailCredentialScope.ImapAndSmtp
                && providers.For(account).Inspect(account).CanSend) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var refreshed = await upstream.PullEmailAsync(userId, account.EmailAddress, timeout.Token);
            // A locally imported credential remains usable when the platform does
            // not know this account or is temporarily unavailable.
            if (refreshed.Enabled && !refreshed.Succeeded
                && string.IsNullOrWhiteSpace(account.Password) && string.IsNullOrWhiteSpace(account.OAuthRefreshToken))
                throw new InvalidOperationException("该邮箱授权更新失败，请稍后重试。");
            await context.Entry(account).ReloadAsync(timeout.Token);
            if (!account.IsEnabled) throw new InvalidOperationException("该邮箱已停用。");
            await discovery.DiscoverAsync(account, timeout.Token);
            var receiving = account.CredentialScope is MailCredentialScope.Imap or MailCredentialScope.ImapAndSmtp;
            var success = await providers.For(account).TestOutgoingConnectionAsync(account, timeout.Token);
            account.CredentialLastCheckedAt = DateTime.UtcNow;
            if (success)
            {
                account.CredentialScope = receiving ? MailCredentialScope.ImapAndSmtp : MailCredentialScope.Smtp;
                account.CredentialDetectionStatus = receiving ? "ImapSmtpVerified" : "SmtpVerified";
            }
            else
            {
                account.CredentialScope = receiving ? MailCredentialScope.Imap : MailCredentialScope.Unknown;
                account.CredentialDetectionStatus = receiving ? "ImapVerified" : "VerificationFailed";
            }
            await context.SaveChangesAsync(cancellationToken);
            if (!success) throw new InvalidOperationException("发件验证未通过，请检查该账号授权和网络后重试。");
        }
        finally { gate.Release(); }
    }
}
