using MailArchiver.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
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

    Task<bool> TestAsync(MailAccount account, MsaAccessToken token, CancellationToken cancellationToken);
    Task<bool> TestWithPasswordAsync(MailAccount account, string password, CancellationToken cancellationToken);
}

public sealed class OutlookSmtpMailSender : IOutlookSmtpMailSender
{
    private readonly INetworkMailProxyFactory _networkMail;

    public OutlookSmtpMailSender(INetworkMailProxyFactory networkMail)
        => _networkMail = networkMail;

    public async Task SendAsync(
        MailAccount account,
        MimeMessage message,
        MsaAccessToken token,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
            MailCertificatePolicy.IsAccepted(errors, chain);
        await ConnectAsync(client, cancellationToken);
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
        client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
            MailCertificatePolicy.IsAccepted(errors, chain);
        await ConnectAsync(client, cancellationToken);
        client.AuthenticationMechanisms.Remove("GSSAPI");
        client.AuthenticationMechanisms.Remove("NEGOTIATE");
        await client.AuthenticateAsync(account.Username ?? account.EmailAddress, password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task<bool> TestAsync(MailAccount account, MsaAccessToken token, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        try
        {
            client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
                MailCertificatePolicy.IsAccepted(errors, chain);
            await ConnectAsync(client, cancellationToken);
            client.AuthenticationMechanisms.Remove("GSSAPI");
            client.AuthenticationMechanisms.Remove("NEGOTIATE");
            await client.AuthenticateAsync(new SaslMechanismOAuth2(token.Username, token.AccessToken), cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TestWithPasswordAsync(MailAccount account, string password, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        try
        {
            client.ServerCertificateValidationCallback = static (_, _, chain, errors) =>
                MailCertificatePolicy.IsAccepted(errors, chain);
            await ConnectAsync(client, cancellationToken);
            client.AuthenticationMechanisms.Remove("GSSAPI");
            client.AuthenticationMechanisms.Remove("NEGOTIATE");
            await client.AuthenticateAsync(account.Username ?? account.EmailAddress, password, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Task ConnectAsync(SmtpClient client, CancellationToken cancellationToken)
        => _networkMail.ConnectAsync(client, "smtp-mail.outlook.com", 587,
            token => client.ConnectAsync("smtp-mail.outlook.com", 587, SecureSocketOptions.StartTls, token),
            cancellationToken);
}
