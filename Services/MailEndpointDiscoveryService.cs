using System.Net;
using System.Net.Mail;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface IMailEndpointDiscoveryService
{
    Task DiscoverAsync(MailAccount account, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves custom-domain IMAP/SMTP endpoints from the two common Mozilla
/// Autoconfig locations. It never opens a mailbox or sends credentials. A
/// conventional-host fallback remains available when discovery is unavailable.
/// </summary>
public sealed class MailEndpointDiscoveryService : IMailEndpointDiscoveryService
{
    private const int MaxConfigBytes = 64 * 1024;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MailEndpointDiscoveryService> _logger;

    public MailEndpointDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ILogger<MailEndpointDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DiscoverAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.MailProviderKind != MailProviderKind.Custom)
            return;

        if (account.EndpointDiscoveryLastCheckedAt is { } checkedAt &&
            checkedAt > DateTime.UtcNow.Subtract(RetryAfter))
            return;

        var domain = GetDomain(account.EmailAddress);
        if (domain is null)
        {
            account.EndpointDiscoveryStatus = "InvalidDomain";
            account.EndpointDiscoveryLastCheckedAt = DateTime.UtcNow;
            return;
        }

        var incomingDiscovered = false;
        var outgoingDiscovered = false;
        try
        {
            var client = _httpClientFactory.CreateClient("MailAutoconfig");
            foreach (var url in GetConfigUrls(domain))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.ParseAdd("application/xml, text/xml;q=0.9, */*;q=0.1");
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var xml = await ReadLimitedAsync(response, cancellationToken);
                    if (xml is null)
                        continue;

                    var found = ApplyConfig(account, xml);
                    incomingDiscovered = incomingDiscovered || found.Incoming;
                    outgoingDiscovered = outgoingDiscovered || found.Outgoing;
                    if (incomingDiscovered && outgoingDiscovered)
                        break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A timed-out config host is equivalent to an unavailable host.
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogDebug(exception, "Autoconfig request failed for {Domain}", domain);
                }
                catch (XmlException exception)
                {
                    _logger.LogDebug(exception, "Autoconfig response was not valid XML for {Domain}", domain);
                }
            }
        }
        finally
        {
            account.EndpointDiscoveryLastCheckedAt = DateTime.UtcNow;
            account.EndpointDiscoveryStatus = incomingDiscovered || outgoingDiscovered ? "Discovered" : "Fallback";
        }
    }

    private static IEnumerable<string> GetConfigUrls(string domain)
    {
        yield return $"https://autoconfig.{domain}/mail/config-v1.1.xml";
        yield return $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml";
    }

    private static async Task<string?> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxConfigBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static (bool Incoming, bool Outgoing) ApplyConfig(MailAccount account, string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var incoming = FindServer(document, "incomingServer", "imap");
        var outgoing = FindServer(document, "outgoingServer", "smtp");
        var incomingApplied = false;
        var outgoingApplied = false;

        if (incoming is not null)
        {
            account.ImapServer = incoming.Value.Host;
            account.ImapPort = incoming.Value.Port;
            account.UseSSL = incoming.Value.UseTls;
            incomingApplied = true;
        }

        if (outgoing is not null)
        {
            account.SmtpServer = outgoing.Value.Host;
            account.SmtpPort = outgoing.Value.Port;
            account.SmtpUseSSL = outgoing.Value.UseTls;
            outgoingApplied = true;
        }

        return (incomingApplied, outgoingApplied);
    }

    private static (string Host, int Port, bool UseTls)? FindServer(
        XDocument document,
        string elementName,
        string type)
    {
        var element = document
            .Descendants()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Attribute("type")?.Value.Equals(type, StringComparison.OrdinalIgnoreCase) == true);
        if (element is null)
            return null;

        var host = ChildValue(element, "hostname");
        var portText = ChildValue(element, "port");
        if (!IsSafeHost(host) || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            return null;

        var socketType = ChildValue(element, "socketType");
        var useTls = !socketType.Equals("plain", StringComparison.OrdinalIgnoreCase)
            && !socketType.Equals("none", StringComparison.OrdinalIgnoreCase);
        return (host, port, useTls);
    }

    private static string ChildValue(XElement parent, string name)
        => parent.Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;

    private static bool IsSafeHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253)
            return false;
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(value, out var address))
            return !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
        return Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.Basic;
    }

    private static string? GetDomain(string email)
    {
        try
        {
            return new MailAddress(email.Trim()).Host.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
