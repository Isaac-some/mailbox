using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Services.Providers.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services;

public sealed class OutboundMailService : IOutboundMailService
{
    private readonly IMsaTokenManager _tokenManager;
    private readonly IExternalOAuthTokenManager _externalOAuthTokenManager;
    private readonly IOutlookRestMailSender _outlookRestMailSender;
    private readonly ImapConnectionFactory _imapConnectionFactory;
    private readonly IImapFolderService _folderService;
    private readonly EmailCoreService _emailCoreService;
    private readonly ICredentialEncryptionService _credentialEncryptionService;
    private readonly OutboundMailOptions _options;
    private readonly ILogger<OutboundMailService> _logger;

    public OutboundMailService(
        IMsaTokenManager tokenManager,
        IExternalOAuthTokenManager externalOAuthTokenManager,
        IOutlookRestMailSender outlookRestMailSender,
        ImapConnectionFactory imapConnectionFactory,
        IImapFolderService folderService,
        EmailCoreService emailCoreService,
        ICredentialEncryptionService credentialEncryptionService,
        IOptions<OutboundMailOptions> options,
        ILogger<OutboundMailService> logger)
    {
        _tokenManager = tokenManager;
        _externalOAuthTokenManager = externalOAuthTokenManager;
        _outlookRestMailSender = outlookRestMailSender;
        _imapConnectionFactory = imapConnectionFactory;
        _folderService = folderService;
        _emailCoreService = emailCoreService;
        _credentialEncryptionService = credentialEncryptionService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OutboundMailResult> SendAsync(
        MailAccount account,
        OutboundMailMessage request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("当前运行环境未启用发件功能。");
        if (!CanAttemptSend(account))
            throw new InvalidOperationException("该账号没有可用的发件凭据。");

        var message = BuildMimeMessage(account, request);
        var sentCopySavedByProvider = await SendViaSmtpAsync(account, message, cancellationToken);

        var sentCopySaved = sentCopySavedByProvider;
        if (!sentCopySavedByProvider)
        {
            try
            {
                await SaveToSentFolderAsync(account, message, cancellationToken);
                sentCopySaved = true;
            }
            catch (Exception ex)
            {
                sentCopySaved = false;
                _logger.LogWarning(ex, "Mail {MessageId} was sent but could not be appended to Sent for account {AccountId}",
                    message.MessageId, account.Id);
            }
        }

        try
        {
            await _emailCoreService.ArchiveEmailAsync(account, message, isOutgoing: true, folderName: "Sent");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mail {MessageId} was sent but could not be archived locally for account {AccountId}",
                message.MessageId, account.Id);
        }

        return new OutboundMailResult(sentCopySaved, message.MessageId!);
    }

    public static MimeMessage BuildMimeMessage(MailAccount account, OutboundMailMessage request)
    {
        var message = new MimeMessage
        {
            Subject = request.Subject.Trim(),
            Date = DateTimeOffset.Now,
            MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId()
        };
        message.From.Add(new MailboxAddress(account.Name, account.EmailAddress));
        message.To.AddRange(request.To.Select(MailboxAddress.Parse));
        message.Cc.AddRange(request.Cc.Select(MailboxAddress.Parse));

        var body = new BodyBuilder { TextBody = request.Body };
        foreach (var attachment in request.Attachments)
        {
            body.Attachments.Add(
                Path.GetFileName(attachment.FileName),
                attachment.Content,
                ContentType.Parse(string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType));
        }
        message.Body = body.ToMessageBody();
        return message;
    }

    public static bool CanAttemptSend(MailAccount account)
    {
        if (account.Provider == ProviderType.MSA)
            return MsaOAuthScopePolicy.CanAttemptSend(account.OAuthRefreshToken, account.OAuthGrantedScopes);

        return account.Provider == ProviderType.IMAP
            && SupportedMailProviderPolicy.TryResolve(account.EmailAddress, out _)
            && (!string.IsNullOrWhiteSpace(account.Password)
                || ExternalOAuthProviderPolicy.HasUsableCredentials(account));
    }

    private async Task<bool> SendViaSmtpAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        var sentCopySavedByProvider = false;
        if (account.Provider == ProviderType.MSA)
        {
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
            var token = await _tokenManager.GetAccessTokenAsync(account, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(account.OAuthGrantedScopes) &&
                !MsaOAuthScopePolicy.CanSend(account.OAuthGrantedScopes))
                throw new InvalidOperationException("这个 Outlook Refresh Token 没有 SMTP.Send 权限，无法发件。");

            try
            {
                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
            }
            catch (AuthenticationException ex) when (IsSmtpClientAuthenticationDisabled(ex))
            {
                await SendViaOutlookRestFallbackAsync(account, message, token.AccessToken, cancellationToken);
                return true;
            }
            catch (AuthenticationException)
            {
                token = await _tokenManager.GetAccessTokenAsync(account, forceRefresh: true, cancellationToken);
                try
                {
                    await client.AuthenticateAsync(
                        new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
                }
                catch (AuthenticationException ex) when (IsSmtpClientAuthenticationDisabled(ex))
                {
                    await SendViaOutlookRestFallbackAsync(account, message, token.AccessToken, cancellationToken);
                    return true;
                }
            }
        }
        else
        {
            if (!SupportedMailProviderPolicy.TryResolve(account.EmailAddress, out var preset))
                throw new InvalidOperationException("该邮箱服务商不支持发件。");

            await client.ConnectAsync(
                preset.SmtpServer,
                preset.SmtpPort,
                preset.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
                cancellationToken);
            sentCopySavedByProvider = preset.SmtpSavesSentCopy;
            if (ExternalOAuthProviderPolicy.HasUsableCredentials(account))
            {
                var token = await _externalOAuthTokenManager.GetAccessTokenAsync(
                    account, cancellationToken: cancellationToken);
                try
                {
                    await client.AuthenticateAsync(
                        new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
                }
                catch (AuthenticationException)
                {
                    token = await _externalOAuthTokenManager.GetAccessTokenAsync(
                        account, forceRefresh: true, cancellationToken: cancellationToken);
                    await client.AuthenticateAsync(
                        new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
                }
            }
            else
            {
                var password = _credentialEncryptionService.Decrypt(account.Password
                    ?? throw new InvalidOperationException("该账号没有应用密码。"));
                await client.AuthenticateAsync(account.Username ?? account.EmailAddress, password, cancellationToken);
            }
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        _logger.LogInformation("Sent mail {MessageId} from account {AccountId}", message.MessageId, account.Id);
        return sentCopySavedByProvider;
    }

    internal static bool IsSmtpClientAuthenticationDisabled(AuthenticationException exception)
        => exception.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("5.7.139", StringComparison.OrdinalIgnoreCase);

    private async Task SendViaOutlookRestFallbackAsync(
        MailAccount account,
        MimeMessage message,
        string accessToken,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "SMTP AUTH is disabled for account {AccountId}; sending through Outlook REST Mail.Send",
            account.Id);
        await _outlookRestMailSender.SendAsync(account, message, accessToken, cancellationToken);
        _logger.LogInformation("Sent mail {MessageId} through Outlook REST for account {AccountId}",
            message.MessageId, account.Id);
    }

    private async Task SaveToSentFolderAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        using var client = _imapConnectionFactory.CreateImapClient(account.Name);
        await _imapConnectionFactory.ConnectWithFallbackAsync(
            client,
            account.ImapServer ?? "outlook.office365.com",
            account.ImapPort ?? 993,
            useSSL: true,
            account.Name);
        await _imapConnectionFactory.AuthenticateClientAsync(client, account);

        IMailFolder? sentFolder = null;
        try
        {
            sentFolder = client.GetFolder(SpecialFolder.Sent);
        }
        catch (Exception ex) when (ex is FolderNotFoundException or NotSupportedException)
        {
            // Some servers do not advertise SPECIAL-USE; use the existing localized fallback.
        }

        if (sentFolder is null)
        {
            var folders = await _folderService.GetAllFoldersAsync(client, account.Name);
            sentFolder = folders.FirstOrDefault(_folderService.IsOutgoingFolder);
        }

        if (sentFolder is null)
            throw new InvalidOperationException("邮箱服务商没有返回可写入的“已发送”文件夹。");

        await sentFolder.AppendAsync(message, MessageFlags.Seen, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
