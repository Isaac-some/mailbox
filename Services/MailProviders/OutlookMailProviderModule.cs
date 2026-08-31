using MailAddress = System.Net.Mail.MailAddress;
using MailArchiver.Models;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MailArchiver.Services.MailProviders;

public sealed class OutlookMailProviderModule : IMailProviderModule
{
    private static readonly HashSet<string> KnownDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.com", "hotmail.com", "live.com", "msn.com"
    };

    private readonly IMsaTokenManager _tokenManager;
    private readonly IOutlookGraphMailSender _graphMailSender;
    private readonly IOutlookSmtpMailSender? _smtpMailSender;

    public OutlookMailProviderModule(
        IMsaTokenManager tokenManager,
        IOutlookGraphMailSender graphMailSender,
        IOutlookSmtpMailSender? smtpMailSender = null)
    {
        _tokenManager = tokenManager;
        _graphMailSender = graphMailSender;
        _smtpMailSender = smtpMailSender;
    }

    public MailProviderKind Kind => MailProviderKind.Outlook;
    public string DisplayName => "Outlook";

    public bool SupportsAddress(string emailAddress)
    {
        try
        {
            return KnownDomains.Contains(new MailAddress(emailAddress.Trim()).Host);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public ImapEndpoint GetIncomingEndpoint(MailAccount account)
        => new("outlook.office365.com", 993, true);

    public void PrepareAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        account.Provider = ProviderType.MSA;
        account.MailProviderKind = Kind;
        var endpoint = GetIncomingEndpoint(account);
        account.ImapServer = endpoint.Host;
        account.ImapPort = endpoint.Port;
        account.UseSSL = endpoint.UseSsl;
        account.Username = account.EmailAddress.Trim();
        account.Name = MailAccountNamePolicy.Derive(account.EmailAddress);
    }

    public MailAccountCapabilities Inspect(MailAccount account)
    {
        EnsureIdentity(account);
        var canReceive = !string.IsNullOrWhiteSpace(account.OAuthRefreshToken);
        var canSend = MsaOAuthScopePolicy.CanAttemptSend(
            account.OAuthRefreshToken,
            account.OAuthGrantedScopes);
        var action = !canReceive
            ? "请完成 Outlook 授权。"
            : canSend ? null : "请重新授权 Outlook 发件权限。";
        return new MailAccountCapabilities(canReceive, canSend, action);
    }

    public async Task AuthenticateIncomingAsync(
        ImapClient client,
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentity(account);
        client.AuthenticationMechanisms.Remove("GSSAPI");
        client.AuthenticationMechanisms.Remove("NEGOTIATE");
        var token = await _tokenManager.GetAccessTokenAsync(account, cancellationToken: cancellationToken);
        try
        {
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
        }
        catch (AuthenticationException)
        {
            token = await _tokenManager.GetAccessTokenAsync(
                account, forceRefresh: true, cancellationToken: cancellationToken);
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
        }
    }

    public async Task<ProviderSendResult> SendAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentity(account);
        if (!Inspect(account).CanSend)
            throw new InvalidOperationException("Outlook 账号没有可用的发件授权。");

        try
        {
            var token = await _tokenManager.GetGraphAccessTokenAsync(
                account, cancellationToken: cancellationToken);
            try
            {
                await _graphMailSender.SendAsync(message, token.AccessToken, cancellationToken);
            }
            catch (OutlookGraphMailException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                token = await _tokenManager.GetGraphAccessTokenAsync(
                    account, forceRefresh: true, cancellationToken: cancellationToken);
                await _graphMailSender.SendAsync(message, token.AccessToken, cancellationToken);
            }
        }
        catch (Exception ex) when (IsGraphAuthorizationFailure(ex) && _smtpMailSender is not null)
        {
            var smtpToken = await _tokenManager.GetSmtpAccessTokenAsync(
                account, cancellationToken: cancellationToken);
            await _smtpMailSender.SendAsync(account, message, smtpToken, cancellationToken);
            return new ProviderSendResult(SentCopySavedByProvider: false);
        }

        return new ProviderSendResult(SentCopySavedByProvider: true);
    }

    private static bool IsGraphAuthorizationFailure(Exception exception)
        => exception is OutlookGraphAuthorizationException
            || exception is OutlookGraphMailException graphException &&
                graphException.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

    private static void EnsureIdentity(MailAccount account)
    {
        if (account.MailProviderKind != MailProviderKind.Outlook)
            throw new InvalidOperationException("Outlook 模块不能处理其他邮箱服务商的账号。");
    }
}
