using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface IMailEndpointDiscoveryService
{
    Task DiscoverAsync(MailAccount account, CancellationToken cancellationToken = default);
}

/// <summary>Discovers TLS endpoints without credentials and reuses domain knowledge across accounts and restarts.</summary>
public sealed class MailEndpointDiscoveryService : IMailEndpointDiscoveryService
{
    private const int MaxConfigBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DomainLocks = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MailEndpointDiscoveryService> _logger;
    private readonly IMailDomainProfileStore? _profiles;

    public MailEndpointDiscoveryService(IHttpClientFactory httpClientFactory,
        ILogger<MailEndpointDiscoveryService> logger, IMailDomainProfileStore? profiles = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _profiles = profiles;
    }

    public async Task DiscoverAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.MailProviderKind != MailProviderKind.Custom) return;
        var domain = MailDomainProfileStore.DomainOf(account.EmailAddress);
        var gate = DomainLocks.GetOrAdd(domain, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var saved = _profiles?.Find(domain);
            if (saved is not null && (saved.Source == "Manual" || saved.DiscoveredAt > DateTime.UtcNow.Add(
                saved.Incoming is null ? -TimeSpan.FromMinutes(15) : -TimeSpan.FromDays(7))))
            {
                MailDomainProfileStore.Apply(account, saved);
                if (saved.Incoming is null) account.EndpointDiscoveryStatus = "NeedsConfiguration";
                return;
            }
            // Legacy account-only cache must not prevent upgrading a guessed host.
            var incoming = default(MailDomainEndpoint);
            var outgoing = default(MailDomainEndpoint);
            var source = "Fallback";
            using var client = _httpClientFactory.CreateClient("MailAutoconfig");
            foreach (var (url, origin) in GetConfigUrls(domain))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.ParseAdd("application/xml, text/xml;q=0.9");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    // Never accept a redirect to a plaintext configuration source.
                    if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme == "http") continue;
                    var xml = await ReadLimitedAsync(response, timeout.Token);
                    if (xml is null) continue;
                    var found = ParseConfig(xml, domain);
                    incoming ??= found.Incoming;
                    outgoing ??= found.Outgoing;
                    if (found.Incoming is not null || found.Outgoing is not null) source = origin;
                    if (incoming is not null && outgoing is not null) break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (ex is HttpRequestException or XmlException or IOException)
                {
                    _logger.LogDebug("Mail configuration lookup failed: {Category}", ex.GetType().Name);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Keep previously working endpoints on a temporary configuration outage.
            var profile = new MailDomainProfile(domain, incoming ?? saved?.Incoming, outgoing ?? saved?.Outgoing,
                incoming is not null || outgoing is not null ? source : saved?.Source ?? "Fallback", OAuth: saved?.OAuth ?? InferOAuth(incoming?.Host));
            if (_profiles is not null)
            {
                await _profiles.SaveConfigurationAsync(profile, cancellationToken);
                profile = _profiles.Find(domain)!;
            }
            else profile = profile with { DiscoveredAt = DateTime.UtcNow };
            MailDomainProfileStore.Apply(account, profile);
            if (profile.Incoming is null) account.EndpointDiscoveryStatus = "NeedsConfiguration";
        }
        finally { gate.Release(); }
    }

    private static MailArchiver.Services.MailProviders.ExternalOAuthSettings? InferOAuth(string? host)
        => host?.ToLowerInvariant() switch
        {
            "imap.gmail.com" => new("Google", "https://oauth2.googleapis.com/token", false, false),
            "imap.mail.yahoo.com" => new("Yahoo", "https://api.login.yahoo.com/oauth2/get_token", true, true),
            "outlook.office365.com" => new("Microsoft", "https://login.microsoftonline.com/common/oauth2/v2.0/token", false, false),
            _ => null
        };

    private static IEnumerable<(string Url, string Source)> GetConfigUrls(string domain)
    {
        yield return ($"https://autoconfig.{domain}/mail/config-v1.1.xml", "DomainAutoconfig");
        yield return ($"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml", "DomainAutoconfig");
        yield return ($"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}", "ThunderbirdISPDB");
    }

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token);
            if (read == 0) break;
            if (buffer.Length + read > MaxConfigBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static (MailDomainEndpoint? Incoming, MailDomainEndpoint? Outgoing) ParseConfig(string xml, string domain)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxConfigBytes });
        var document = XDocument.Load(reader);
        var provider = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "emailProvider"
            && (!e.Elements().Any(d => d.Name.LocalName == "domain")
                || e.Elements().Any(d => d.Name.LocalName == "domain" && d.Value.Trim().Equals(domain, StringComparison.OrdinalIgnoreCase))));
        return provider is null ? (null, null) : (FindServer(provider, "incomingServer", "imap"), FindServer(provider, "outgoingServer", "smtp"));
    }

    private static MailDomainEndpoint? FindServer(XElement provider, string name, string type)
    {
        // Skip unsupported/plain alternatives rather than rejecting later secure alternatives.
        foreach (var element in provider.Elements().Where(e => e.Name.LocalName == name && (string?)e.Attribute("type") == type))
        {
            var host = ChildValue(element, "hostname").ToLowerInvariant();
            var socket = ChildValue(element, "socketType");
            if (!MailDomainProfileStore.IsSafeHost(host) || !int.TryParse(ChildValue(element, "port"), out var port)
                || port is < 1 or > 65535 || socket is not ("SSL" or "STARTTLS")) continue;
            var username = ChildValue(element, "username");
            if (string.IsNullOrEmpty(username)) username = "%EMAILADDRESS%";
            if (username is not ("%EMAILADDRESS%" or "%EMAILLOCALPART%")) continue;
            return new(host, port, socket == "SSL", username);
        }
        return null;
    }

    private static string ChildValue(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
}
