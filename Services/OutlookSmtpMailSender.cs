using MailArchiver.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services;

public interface IOutlookSmtpMailSender
{
    Task SendAsync(
        MailAccount account,
        MimeMessage message,
        MsaAccessToken token,
        CancellationToken cancellationToken);

    Task SendWithPasswordAsync(
        MailAccount account,
        MimeMessage message,
        string password,
        CancellationToken cancellationToken);
}

public sealed class OutlookSmtpMailSender : IOutlookSmtpMailSender
{
    private readonly MailProxyOptions _mailProxyOptions;

    public OutlookSmtpMailSender(IOptions<MailProxyOptions>? mailProxyOptions = null)
        => _mailProxyOptions = mailProxyOptions?.Value ?? new MailProxyOptions();

    public async Task SendAsync(
        MailAccount account,
        MimeMessage message,
        MsaAccessToken token,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        MailProxyClientFactory.Apply(client, _mailProxyOptions);
        client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
            MailCertificatePolicy.IsAccepted(errors, chain);
        await client.ConnectAsync("smtp-mail.outlook.com", 587, SecureSocketOptions.StartTls, cancellationToken);
        client.AuthenticationMechanisms.Remove("GSSAPI");
        client.AuthenticationMechanisms.Remove("NEGOTIATE");
        await client.AuthenticateAsync(
            new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task SendWithPasswordAsync(
        MailAccount account,
        MimeMessage message,
        string password,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        MailProxyClientFactory.Apply(client, _mailProxyOptions);
        client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
            MailCertificatePolicy.IsAccepted(errors, chain);
        await client.ConnectAsync("smtp-mail.outlook.com", 587, SecureSocketOptions.StartTls, cancellationToken);
        client.AuthenticationMechanisms.Remove("GSSAPI");
        client.AuthenticationMechanisms.Remove("NEGOTIATE");
        await client.AuthenticateAsync(account.Username ?? account.EmailAddress, password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
