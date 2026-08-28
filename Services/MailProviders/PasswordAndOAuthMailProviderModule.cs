using System.Net;
using MailAddress = System.Net.Mail.MailAddress;
using MailArchiver.Models;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailArchiver.Services.MailProviders;

public abstract class PasswordAndOAuthMailProviderModule : IMailProviderModule
{
    private readonly IExternalOAuthTokenManager _tokenManager;
    private readonly ICredentialEncryptionService _credentialEncryption;

    protected PasswordAndOAuthMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption)
    {
        _tokenManager = tokenManager;
        _credentialEncryption = credentialEncryption;
    }

    public abstract MailProviderKind Kind { get; }
    public abstract string DisplayName { get; }
    public abstract ImapEndpoint GetIncomingEndpoint(MailAccount account);
    protected abstract string GetSmtpHost(MailAccount account);
    protected virtual int SmtpPort => 587;
    protected virtual SecureSocketOptions SmtpSocketOptions => SecureSocketOptions.StartTls;
    protected virtual bool SmtpSavesSentCopy => false;
    protected virtual ExternalOAuthSettings? OAuth => null;

    public abstract bool SupportsAddress(string emailAddress);

    public virtual string NormalizeAppPassword(string appPassword)
    {
        ArgumentNullException.ThrowIfNull(appPassword);
        return appPassword;
    }

    public virtual void PrepareAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!SupportsAddress(account.EmailAddress))
            throw new InvalidOperationException($"{account.EmailAddress} 不是 {DisplayName} 邮箱。");

        account.Provider = ProviderType.IMAP;
        account.MailProviderKind = Kind;
        var endpoint = GetIncomingEndpoint(account);
        account.ImapServer = endpoint.Host;
        account.ImapPort = endpoint.Port;
        account.UseSSL = endpoint.UseSsl;
        account.Username = account.EmailAddress.Trim();
        account.Name = MailAccountNamePolicy.Derive(account.EmailAddress);
    }

    public virtual MailAccountCapabilities Inspect(MailAccount account)
    {
        EnsureIdentity(account);
        var hasCredential = HasPassword(account) || HasOAuth(account);
        return new MailAccountCapabilities(
            hasCredential,
            hasCredential,
            hasCredential ? null : $"请补充 {DisplayName} 应用专用密码或 OAuth 授权。");
    }

    public async Task AuthenticateIncomingAsync(
        ImapClient client,
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentity(account);
        RemoveUnsupportedMechanisms(client);

        if (HasOAuth(account))
        {
            await AuthenticateOAuthAsync(client, account, cancellationToken);
            return;
        }

        await AuthenticatePasswordAsync(client, account, cancellationToken);
    }

    public async Task<ProviderSendResult> SendAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentity(account);
        if (!Inspect(account).CanSend)
            throw new InvalidOperationException($"{DisplayName} 账号没有可用的发件凭据。");

        using var client = new SmtpClient();
        await client.ConnectAsync(GetSmtpHost(account), SmtpPort, SmtpSocketOptions, cancellationToken);
        if (HasOAuth(account))
            await AuthenticateOAuthAsync(client, account, cancellationToken);
        else
            await AuthenticatePasswordAsync(client, account, cancellationToken);

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return new ProviderSendResult(SmtpSavesSentCopy);
    }

    protected static string? DomainOf(string emailAddress)
    {
        try
        {
            return new MailAddress(emailAddress.Trim()).Host.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    protected bool HasOAuth(MailAccount account)
        => OAuth is not null && ExternalOAuthTokenManager.HasUsableCredentials(account, OAuth);

    protected static bool HasPassword(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Password);

    protected void EnsureIdentity(MailAccount account)
    {
        if (account.MailProviderKind != Kind)
            throw new InvalidOperationException(
                $"{DisplayName} 模块不能处理服务商为“{account.MailProviderKind?.ToString() ?? "未设置"}”的账号。");
    }

    private static void RemoveUnsupportedMechanisms(MailKit.IMailService client)
    {
        client.AuthenticationMechanisms.Remove("GSSAPI");
        client.AuthenticationMechanisms.Remove("NEGOTIATE");
    }

    private async Task AuthenticateOAuthAsync(
        MailKit.IMailService client,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        var oauth = OAuth ?? throw new InvalidOperationException($"{DisplayName} 不支持 OAuth。");
        var token = await _tokenManager.GetAccessTokenAsync(account, oauth, cancellationToken: cancellationToken);
        try
        {
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
        }
        catch (AuthenticationException)
        {
            token = await _tokenManager.GetAccessTokenAsync(
                account, oauth, forceRefresh: true, cancellationToken: cancellationToken);
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
        }
    }

    private async Task AuthenticatePasswordAsync(
        MailKit.IMailService client,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        var username = account.Username ?? account.EmailAddress;
        var password = NormalizeAppPassword(
            _credentialEncryption.Decrypt(account.Password
                ?? throw new InvalidOperationException($"{DisplayName} 账号没有应用专用密码。")));

        if (client.AuthenticationMechanisms.Contains("PLAIN"))
        {
            try
            {
                await client.AuthenticateAsync(
                    new SaslMechanismPlain(new NetworkCredential(username, password)), cancellationToken);
                return;
            }
            catch (AuthenticationException)
            {
                // Some providers advertise PLAIN but reject it for selected accounts.
            }
        }

        await client.AuthenticateAsync(username, password, cancellationToken);
    }
}
