using MailArchiver.Models;
using MailArchiver.Services.MailProviders;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public interface IMailCredentialVerifier
{
    Task VerifyAsync(MailAccount account, CancellationToken cancellationToken = default);
}

/// <summary>Checks incoming authentication on a detached candidate, without opening a folder or fetching mail.</summary>
public sealed class MailCredentialVerifier(
    IMailProviderRegistry registry,
    IMailEndpointDiscoveryService discovery,
    INetworkMailProxyFactory networkMail, IMailDomainProfileStore? profiles = null) : IMailCredentialVerifier
{
    public async Task VerifyAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new ImapClient { Timeout = 20_000 };
        client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
            MailCertificatePolicy.IsAccepted(errors, chain);
        try
        {
            await discovery.DiscoverAsync(account, timeout.Token);
            var provider = registry.For(account);
            var endpoint = provider.GetIncomingEndpoint(account);
            await networkMail.ConnectAsync(client, endpoint.Host, endpoint.Port,
                token => client.ConnectAsync(endpoint.Host, endpoint.Port,
                    endpoint.SocketOptions ?? (endpoint.UseSsl ? (endpoint.Port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect) : SecureSocketOptions.StartTls),
                    token), timeout.Token);
            await provider.AuthenticateIncomingAsync(client, account, timeout.Token);
            if (profiles is not null) await profiles.RecordAttemptAsync(account, false, true, token: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            account.CredentialLastCheckedAt = DateTime.UtcNow;
            account.CredentialDetectionStatus = "IncomingVerified";
            account.CredentialScope = MailCredentialScope.Imap;
            account.CredentialKind = account.PreferredIncomingAuth == MailAuthenticationMethod.OAuth2
                ? MailCredentialKind.OAuth2RefreshToken
                : account.MailProviderKind == MailProviderKind.Gmail
                    ? MailCredentialKind.GoogleAppPassword : MailCredentialKind.ImapPassword;
            // Dispose closes the transport. No folder selection, mail download or SMTP send occurs.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (profiles is not null) await profiles.RecordAttemptAsync(account, false, false, "TargetUnavailable", cancellationToken);
            throw new TimeoutException("邮箱连接验证超时（20 秒），请检查网络后重试；超时不代表授权码错误。");
        }
        catch (MailCredentialFormatException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is not OperationCanceledException)
        {
            if (profiles is not null) await profiles.RecordAttemptAsync(account, false, false, MailConnectionFailurePolicy.Classify(ex).Code.ToString(), cancellationToken);
            // Do not expose protocol responses: they can contain supplied credentials.
            var failures = ex is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.AsEnumerable() : [ex];
            var networkFailure = failures.Any(error => error is System.IO.IOException
                or System.Net.Sockets.SocketException or HttpRequestException or SslHandshakeException
                or ProxyProtocolException);
            throw new InvalidOperationException(networkFailure
                ? "无法连接邮箱完成校验；请检查网络、代理或邮箱服务设置后重试。"
                : "邮箱登录校验未通过；请检查授权码是否完整有效、邮箱是否匹配，以及所需的 Client ID 和收件权限。", ex);
        }
    }
}
