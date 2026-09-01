using MailArchiver.Models;
using MailKit.Net.Imap;
using MimeKit;

namespace MailArchiver.Services.MailProviders;

public sealed record ImapEndpoint(string Host, int Port, bool UseSsl);

public sealed record MailAccountCapabilities(
    bool CanReceive,
    bool CanSend,
    string? RequiredAction = null);

public sealed record ProviderSendResult(bool SentCopySavedByProvider);

public sealed record ExternalOAuthSettings(
    string ProviderName,
    string TokenEndpoint,
    bool RequiresClientSecret,
    bool RequiresRedirectUri);

public interface IMailProviderModule
{
    MailProviderKind Kind { get; }
    string DisplayName { get; }

    bool SupportsAddress(string emailAddress);
    string NormalizeAppPassword(string appPassword)
    {
        ArgumentNullException.ThrowIfNull(appPassword);
        return appPassword;
    }
    ImapEndpoint GetIncomingEndpoint(MailAccount account);
    void PrepareAccount(MailAccount account);
    MailAccountCapabilities Inspect(MailAccount account);

    Task AuthenticateIncomingAsync(
        ImapClient client,
        MailAccount account,
        CancellationToken cancellationToken = default);

    Task<bool> TestOutgoingConnectionAsync(
        MailAccount account,
        CancellationToken cancellationToken = default);

    Task<ProviderSendResult> SendAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken = default);
}

public interface IMailProviderRegistry
{
    IMailProviderModule For(MailProviderKind kind);
    IMailProviderModule For(MailAccount account);
    IMailProviderModule Detect(string emailAddress);
}
