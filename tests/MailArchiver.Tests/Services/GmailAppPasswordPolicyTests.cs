using System.Net;
using System.Net.Sockets;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MailArchiver.Tests.Services;

public class GmailAppPasswordPolicyTests
{
    private static IMailProviderModule CreateGmailModule()
        => new GmailMailProviderModule(null!, null!);

    [Fact]
    public void Gmail_normalizes_the_grouped_app_password_shown_by_Google()
    {
        var normalized = CreateGmailModule().NormalizeAppPassword("abcd efgh ijkl mnop");

        Assert.Equal("abcdefghijklmnop", normalized);
    }

    [Theory]
    [InlineData("abcdefghijklmno")]
    [InlineData("abcdefghijklmnopq")]
    public void Gmail_rejects_app_passwords_that_are_not_exactly_16_characters(string password)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateGmailModule().NormalizeAppPassword(password));

        Assert.Equal(
            "Gmail 应用专用密码去除空白和不可见格式字符后必须恰好是 16 位；请使用 Google 生成的应用专用密码，不是 Google 登录密码。",
            exception.Message);
    }

    [Fact]
    public void Gmail_removes_unicode_spacing_and_invisible_format_characters_before_validating()
    {
        var normalized = CreateGmailModule().NormalizeAppPassword(
            "\tabcd\u00a0efgh\u2003\u200bijkl\ufeff\u2060\r\nmnop ");

        Assert.Equal("abcdefghijklmnop", normalized);
    }

    [Theory]
    [InlineData(MailProviderKind.Yahoo, " short yahoo password ")]
    [InlineData(MailProviderKind.Gmx, "short-gmx-password")]
    public void Non_Gmail_modules_do_not_apply_the_16_character_rule(
        MailProviderKind kind,
        string password)
    {
        IMailProviderModule module = kind == MailProviderKind.Yahoo
            ? new YahooMailProviderModule(null!, null!)
            : new GmxMailProviderModule(null!, null!);

        Assert.Equal(password, module.NormalizeAppPassword(password));
    }

    [Fact]
    public void Gmail_OAuth_credentials_do_not_require_an_app_password()
    {
        var account = new MailAccount
        {
            EmailAddress = "oauth@gmail.com",
            Provider = ProviderType.IMAP,
            MailProviderKind = MailProviderKind.Gmail,
            ClientId = "client-id",
            OAuthRefreshToken = "refresh-token"
        };

        Assert.True(CreateGmailModule().Inspect(account).CanReceive);
        Assert.True(CreateGmailModule().Inspect(account).CanSend);
    }

    [Fact]
    public async Task Gmail_IMAP_authentication_normalizes_a_legacy_stored_app_password()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receivedPassword = CaptureImapPasswordAsync(listener);

        using var client = new ImapClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.None);
        var module = new GmailMailProviderModule(null!, new PassthroughCredentialEncryptionService());
        var account = new MailAccount
        {
            EmailAddress = "legacy@gmail.com",
            Username = "legacy@gmail.com",
            Password = "abcd efgh ijkl mnop",
            Provider = ProviderType.IMAP,
            MailProviderKind = MailProviderKind.Gmail
        };

        await module.AuthenticateIncomingAsync(client, account);
        await client.DisconnectAsync(true);

        Assert.Equal("abcdefghijklmnop", await receivedPassword);
    }

    [Fact]
    public async Task Gmail_SMTP_authentication_normalizes_a_legacy_stored_app_password()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receivedPassword = CaptureSmtpPasswordAsync(listener);
        var module = new LocalSmtpGmailModule(port, new PassthroughCredentialEncryptionService());
        var account = new MailAccount
        {
            EmailAddress = "legacy@gmail.com",
            Username = "legacy@gmail.com",
            Password = "abcd efgh ijkl mnop",
            Provider = ProviderType.IMAP,
            MailProviderKind = MailProviderKind.Gmail
        };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account.EmailAddress));
        message.To.Add(MailboxAddress.Parse("receiver@example.com"));
        message.Subject = "SMTP app-password normalization";
        message.Body = new TextPart("plain") { Text = "test" };

        await module.SendAsync(account, message);

        Assert.Equal("abcdefghijklmnop", await receivedPassword);
    }

    private static async Task<string> CaptureImapPasswordAsync(TcpListener listener)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
        await writer.WriteLineAsync("* OK Gmail app-password test server ready");

        string? receivedPassword = null;
        while (await reader.ReadLineAsync() is { } command)
        {
            var parts = command.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            var tag = parts[0];
            if (command.Contains(" CAPABILITY", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN");
                await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
            }
            else if (command.Contains(" AUTHENTICATE PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                var payload = parts.Length == 4 ? parts[3] : null;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    await writer.WriteLineAsync("+");
                    payload = await reader.ReadLineAsync();
                }

                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(payload!)).Split('\0');
                receivedPassword = credentials[^1];
                await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed");
            }
            else if (command.Contains(" LOGOUT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* BYE logging out");
                await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                break;
            }
            else
            {
                await writer.WriteLineAsync($"{tag} BAD unsupported command");
            }
        }

        return receivedPassword ?? throw new InvalidOperationException("IMAP server did not receive a password.");
    }

    private static async Task<string> CaptureSmtpPasswordAsync(TcpListener listener)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
        await writer.WriteLineAsync("220 localhost SMTP test server ready");

        string? receivedPassword = null;
        while (await reader.ReadLineAsync() is { } command)
        {
            if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-localhost");
                await writer.WriteLineAsync("250 AUTH PLAIN");
            }
            else if (command.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                var payload = command["AUTH PLAIN".Length..].Trim();
                if (payload.Length == 0)
                {
                    await writer.WriteLineAsync("334");
                    payload = await reader.ReadLineAsync() ?? string.Empty;
                }

                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Split('\0');
                receivedPassword = credentials[^1];
                await writer.WriteLineAsync("235 2.7.0 Authentication successful");
            }
            else if (command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                     command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 2.1.0 OK");
            }
            else if (command.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".") { }
                await writer.WriteLineAsync("250 2.0.0 queued");
            }
            else if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 2.0.0 bye");
                break;
            }
            else
            {
                await writer.WriteLineAsync("250 OK");
            }
        }

        return receivedPassword ?? throw new InvalidOperationException("SMTP server did not receive a password.");
    }

    private sealed class PassthroughCredentialEncryptionService : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue;
    }

    private sealed class LocalSmtpGmailModule : GmailMailProviderModule
    {
        private readonly int _port;

        public LocalSmtpGmailModule(int port, ICredentialEncryptionService credentialEncryption)
            : base(null!, credentialEncryption)
        {
            _port = port;
        }

        protected override string GetSmtpHost(MailAccount account) => IPAddress.Loopback.ToString();
        protected override int SmtpPort => _port;
        protected override SecureSocketOptions SmtpSocketOptions => SecureSocketOptions.None;
    }
}
