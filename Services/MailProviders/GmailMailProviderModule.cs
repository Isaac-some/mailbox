using MailArchiver.Models;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.MailProviders;

public class GmailMailProviderModule : PasswordAndOAuthMailProviderModule
{
    private static readonly ExternalOAuthSettings OAuthSettings = new(
        "Gmail",
        "https://oauth2.googleapis.com/token",
        RequiresClientSecret: false,
        RequiresRedirectUri: false);

    public GmailMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption,
        IOptions<MailProxyOptions>? mailProxyOptions = null)
        : base(tokenManager, credentialEncryption, mailProxyOptions) { }

    public override MailProviderKind Kind => MailProviderKind.Gmail;
    public override string DisplayName => "Gmail";
    public override ImapEndpoint GetIncomingEndpoint(MailAccount account) => new("imap.gmail.com", 993, true);
    protected override string GetSmtpHost(MailAccount account) => "smtp.gmail.com";
    protected override bool SmtpSavesSentCopy => true;
    protected override ExternalOAuthSettings OAuth => OAuthSettings;

    public override bool SupportsAddress(string emailAddress)
        => DomainOf(emailAddress) is "gmail.com" or "googlemail.com";

    public override string NormalizeAppPassword(string appPassword)
    {
        ArgumentNullException.ThrowIfNull(appPassword);
        var normalized = string.Concat(appPassword.Where(character =>
            !char.IsWhiteSpace(character) &&
            char.GetUnicodeCategory(character) != UnicodeCategory.Format));
        if (normalized.Length != 16)
        {
            throw new InvalidOperationException(
                "Gmail 应用专用密码去除空白和不可见格式字符后必须恰好是 16 位；请使用 Google 生成的应用专用密码，不是 Google 登录密码。");
        }

        return normalized;
    }
}
