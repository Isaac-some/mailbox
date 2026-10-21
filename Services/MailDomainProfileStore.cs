using System.Globalization;
using System.Net;
using System.Text.Json;
using MailArchiver.Models;
using MailArchiver.Services.MailProviders;
using MailKit.Security;

namespace MailArchiver.Services;

public sealed record MailDomainEndpoint(string Host, int Port, bool ImplicitTls, string Username = "%EMAILADDRESS%")
{
    public SecureSocketOptions SocketOptions => ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}

// No mailbox addresses, credentials or tokens belong in this shared domain record.
public sealed record MailDomainProfile(
    string Domain,
    MailDomainEndpoint? Incoming = null,
    MailDomainEndpoint? Outgoing = null,
    string Source = "Fallback",
    DateTime? DiscoveredAt = null,
    DateTime? IncomingVerifiedAt = null,
    DateTime? OutgoingVerifiedAt = null,
    MailAuthenticationMethod IncomingAuth = MailAuthenticationMethod.Unknown,
    MailAuthenticationMethod OutgoingAuth = MailAuthenticationMethod.Unknown,
    DateTime? LastAttemptAt = null,
    string? LastFailure = null,
    ExternalOAuthSettings? OAuth = null,
    string? IncomingFailure = null,
    string? OutgoingFailure = null);

public interface IMailDomainProfileStore
{
    MailDomainProfile? Find(string domain);
    IReadOnlyList<MailDomainProfile> GetAll();
    Task SaveConfigurationAsync(MailDomainProfile profile, CancellationToken token = default);
    Task RecordAttemptAsync(MailAccount account, bool outgoing, bool success, string? failure = null, CancellationToken token = default);
}

public sealed class MailDomainProfileStore : IMailDomainProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _readGate = new();
    private Dictionary<string, MailDomainProfile> _profiles;

    public MailDomainProfileStore(IWebHostEnvironment environment)
        : this(Path.Combine(LocalAppStoragePathPolicy.ResolveWritableRoot(environment.ContentRootPath), "mail-domain-profiles.json")) { }

    public MailDomainProfileStore(string path)
    {
        _path = path;
        _profiles = File.Exists(path)
            ? (JsonSerializer.Deserialize<List<MailDomainProfile>>(File.ReadAllText(path)) ?? [])
                .ToDictionary(p => NormalizeDomain(p.Domain), StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public MailDomainProfile? Find(string domain)
    {
        lock (_readGate) return _profiles.GetValueOrDefault(NormalizeDomain(domain));
    }

    public IReadOnlyList<MailDomainProfile> GetAll()
    {
        lock (_readGate) return _profiles.Values.OrderBy(p => p.Domain).ToArray();
    }

    public Task SaveConfigurationAsync(MailDomainProfile profile, CancellationToken token = default)
    {
        ValidateEndpoint(profile.Incoming);
        ValidateEndpoint(profile.Outgoing);
        if (profile.OAuth is { } oauth && (!Uri.TryCreate(oauth.TokenEndpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !IsSafeHost(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)))
            throw new InvalidOperationException("OAuth 令牌地址必须是有效的公网 HTTPS 地址。");
        var normalized = profile with { Domain = NormalizeDomain(profile.Domain) };
        return UpdateAsync(normalized.Domain, old => normalized with
        {
            // Observations are valid only for the same endpoint and OAuth configuration.
            IncomingVerifiedAt = old?.Incoming == normalized.Incoming && old?.OAuth == normalized.OAuth ? old?.IncomingVerifiedAt : null,
            OutgoingVerifiedAt = old?.Outgoing == normalized.Outgoing && old?.OAuth == normalized.OAuth ? old?.OutgoingVerifiedAt : null,
            IncomingAuth = old?.Incoming == normalized.Incoming && old?.OAuth == normalized.OAuth ? old?.IncomingAuth ?? MailAuthenticationMethod.Unknown : MailAuthenticationMethod.Unknown,
            OutgoingAuth = old?.Outgoing == normalized.Outgoing && old?.OAuth == normalized.OAuth ? old?.OutgoingAuth ?? MailAuthenticationMethod.Unknown : MailAuthenticationMethod.Unknown,
            DiscoveredAt = DateTime.UtcNow
        }, token);
    }

    public Task RecordAttemptAsync(MailAccount account, bool outgoing, bool success, string? failure = null, CancellationToken token = default)
    {
        var domain = DomainOf(account.EmailAddress);
        return UpdateAsync(domain, existing =>
        {
            var profile = existing ?? new MailDomainProfile(domain,
                string.IsNullOrWhiteSpace(account.ImapServer) ? null : new(account.ImapServer, account.ImapPort ?? 993, (account.ImapPort ?? 993) == 993),
                string.IsNullOrWhiteSpace(account.SmtpServer) ? null : new(account.SmtpServer, account.SmtpPort ?? 587, account.SmtpPort == 465),
                "BuiltIn");
            // A conventional host becomes reusable only after a real authenticated connection.
            if (success && !outgoing && profile.Incoming is null && !string.IsNullOrWhiteSpace(account.ImapServer))
                profile = profile with { Incoming = new(account.ImapServer, account.ImapPort ?? 993, account.ImapPort != 143),
                    Source = profile.Source == "Fallback" ? "VerifiedFallback" : profile.Source, DiscoveredAt = DateTime.UtcNow };
            if (success && outgoing && profile.Outgoing is null && !string.IsNullOrWhiteSpace(account.SmtpServer))
                profile = profile with { Outgoing = new(account.SmtpServer, account.SmtpPort ?? 587, account.SmtpPort == 465),
                    Source = profile.Source == "Fallback" ? "VerifiedFallback" : profile.Source, DiscoveredAt = DateTime.UtcNow };
            // A result from a connection already in flight must not certify newer settings.
            var endpointMatches = outgoing
                ? profile.Outgoing?.Host == account.SmtpServer && profile.Outgoing?.Port == account.SmtpPort
                : profile.Incoming?.Host == account.ImapServer && profile.Incoming?.Port == account.ImapPort;
            var now = DateTime.UtcNow;
            return profile with
            {
                IncomingVerifiedAt = success && !outgoing && endpointMatches ? now : profile.IncomingVerifiedAt,
                OutgoingVerifiedAt = success && outgoing && endpointMatches ? now : profile.OutgoingVerifiedAt,
                IncomingAuth = success && !outgoing && endpointMatches ? account.PreferredIncomingAuth : profile.IncomingAuth,
                OutgoingAuth = success && outgoing && endpointMatches ? account.PreferredOutgoingAuth : profile.OutgoingAuth,
                LastAttemptAt = now,
                LastFailure = success ? null : failure,
                IncomingFailure = !outgoing ? (success ? null : failure) : profile.IncomingFailure,
                OutgoingFailure = outgoing ? (success ? null : failure) : profile.OutgoingFailure
            };
        }, token);
    }

    private async Task UpdateAsync(string domain, Func<MailDomainProfile?, MailDomainProfile> update, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            Dictionary<string, MailDomainProfile> next;
            lock (_readGate) next = new(_profiles, StringComparer.OrdinalIgnoreCase);
            next[domain] = update(next.GetValueOrDefault(domain));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next.Values, new JsonSerializerOptions { WriteIndented = true }), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, overwrite: true);
            lock (_readGate) _profiles = next;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public static string DomainOf(string email) => NormalizeDomain(new System.Net.Mail.MailAddress(email).Host);

    public static string NormalizeDomain(string domain)
    {
        var normalized = new IdnMapping().GetAscii(domain.Trim().TrimStart('@').TrimEnd('.')).ToLowerInvariant();
        if (!IsSafeHost(normalized)) throw new InvalidOperationException("请输入有效的公网邮箱域名。");
        return normalized;
    }

    public static bool IsSafeHost(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 253
            && value.Contains('.') && !value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !IPAddress.TryParse(value, out _) && Uri.CheckHostName(value) == UriHostNameType.Dns;

    private static void ValidateEndpoint(MailDomainEndpoint? endpoint)
    {
        if (endpoint is not null && (!IsSafeHost(endpoint.Host) || endpoint.Port is < 1 or > 65535
            || endpoint.Username is not ("%EMAILADDRESS%" or "%EMAILLOCALPART%")))
            throw new InvalidOperationException("邮箱服务器需填写有效的公网主机名、端口和用户名格式。");
    }

    public static void Apply(MailAccount account, MailDomainProfile profile)
    {
        if (profile.Incoming is { } incoming)
        {
            account.ImapServer = incoming.Host;
            account.ImapPort = incoming.Port;
            account.UseSSL = true;
            account.Username = UsernameFor(account.EmailAddress, incoming.Username);
        }
        if (profile.Outgoing is { } outgoing)
        {
            account.SmtpServer = outgoing.Host;
            account.SmtpPort = outgoing.Port;
            account.SmtpUseSSL = true;
        }
        account.EndpointDiscoveryStatus = profile.Source == "Manual" ? "Configured" : "Discovered";
        account.EndpointDiscoveryLastCheckedAt = profile.DiscoveredAt;
        // Domain knowledge prioritizes attempts; it never grants an account capabilities.
        if (account.PreferredIncomingAuth == MailAuthenticationMethod.Unknown) account.PreferredIncomingAuth = profile.IncomingAuth;
        if (account.PreferredOutgoingAuth == MailAuthenticationMethod.Unknown) account.PreferredOutgoingAuth = profile.OutgoingAuth;
    }

    public static string UsernameFor(string email, string pattern)
        => pattern == "%EMAILLOCALPART%" ? email[..email.LastIndexOf('@')] : email;
}
