using System.Net.Mail;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.MailProviders;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public sealed record MailCredentialIntake(
    string Email,
    string Credential,
    string? Domain = null,
    string? ClientId = null);

public sealed record MailCredentialIntakeResult(
    MailAccount Account,
    bool Created,
    MailCredentialKind Kind,
    MailCredentialScope Scope,
    string Status);

/// <summary>
/// Converts the upstream minimal contract (email + credential + optional domain)
/// into a provider account. Type/scope are intentionally conservative: a token
/// shape is only a hint; IMAP/SMTP coverage is confirmed by connection checks.
/// </summary>
public sealed class MailCredentialIntakeService
{
    private readonly MailArchiverDbContext _context;
    private readonly IMailProviderRegistry _registry;
    private readonly ICredentialEncryptionService _encryption;

    public MailCredentialIntakeService(
        MailArchiverDbContext context,
        IMailProviderRegistry registry,
        ICredentialEncryptionService encryption)
    {
        _context = context;
        _registry = registry;
        _encryption = encryption;
    }

    public async Task<MailCredentialIntakeResult> UpsertAsync(
        int userId,
        MailCredentialIntake input,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));

        var email = NormalizeEmail(input.Email);
        if (string.IsNullOrWhiteSpace(input.Credential))
            throw new InvalidOperationException("授权凭据不能为空。");

        var provider = ResolveProvider(email, input.Domain);
        var existing = await _context.MailAccounts
            .Include(a => a.UserMailAccounts)
            .FirstOrDefaultAsync(a => a.EmailAddress.ToLower() == email.ToLower(), cancellationToken);
        if (existing is not null && !existing.UserMailAccounts.Any(link => link.UserId == userId))
            throw new InvalidOperationException("该邮箱已属于其他用户，当前接口不能跨用户覆盖凭据。");

        var created = existing is null;
        var account = existing ?? new MailAccount
        {
            EmailAddress = email,
            Name = MailAccountNamePolicy.Derive(email),
            GroupName = string.Empty,
            Username = email,
            UseSSL = true,
            IsEnabled = enabled,
            LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExcludedFolders = string.Empty,
            Provider = provider.Kind == MailProviderKind.Outlook ? ProviderType.MSA : ProviderType.IMAP,
            MailProviderKind = provider.Kind
        };

        provider.PrepareAccount(account);
        ApplyCredential(account, provider, input.Credential, input.ClientId);

        account.IsEnabled = enabled;
        account.CredentialLastCheckedAt = null;
        if (!string.Equals(account.CredentialDetectionStatus, "OAuthConfigurationRequired", StringComparison.Ordinal))
            account.CredentialDetectionStatus = "PendingVerification";

        if (created)
        {
            _context.MailAccounts.Add(account);
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (!account.UserMailAccounts.Any(link => link.UserId == userId))
        {
            _context.UserMailAccounts.Add(new UserMailAccount
            {
                UserId = userId,
                MailAccountId = account.Id
            });
        }

        // Always persist existing-account credential rotations as well as new
        // ownership links. The initial save above is only needed to obtain a
        // database-generated account ID for a newly created account.
        await _context.SaveChangesAsync(cancellationToken);

        return new MailCredentialIntakeResult(
            account,
            created,
            account.CredentialKind,
            account.CredentialScope,
            account.CredentialDetectionStatus!);
    }

    private void ApplyCredential(
        MailAccount account,
        IMailProviderModule provider,
        string rawCredential,
        string? clientId)
    {
        var credential = rawCredential.Trim();
        var suppliedClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        if (!string.IsNullOrWhiteSpace(suppliedClientId) &&
            !Guid.TryParseExact(suppliedClientId, "D", out _) &&
            provider.Kind == MailProviderKind.Outlook)
        {
            throw new InvalidOperationException("Outlook OAuth2 的 Client ID 必须是有效的 GUID。");
        }

        if (provider.Kind == MailProviderKind.Gmail && LooksLikeGoogleAppPassword(credential))
        {
            if (!string.IsNullOrWhiteSpace(suppliedClientId))
                throw new InvalidOperationException("Google 应用专用密码不需要 Client ID；请将 Client ID 留空。");
            account.Password = _encryption.Encrypt(provider.NormalizeAppPassword(credential));
            account.CredentialKind = MailCredentialKind.GoogleAppPassword;
            account.CredentialScope = MailCredentialScope.Unknown;
            return;
        }

        var oauthCandidate = LooksLikeOAuthRefreshToken(credential) || !string.IsNullOrWhiteSpace(suppliedClientId);
        if (provider.Kind == MailProviderKind.Outlook &&
            LooksLikeOAuthRefreshToken(credential) &&
            string.IsNullOrWhiteSpace(suppliedClientId))
        {
            throw new InvalidOperationException("Outlook OAuth2 Refresh Token 必须同时提供 Client ID。");
        }

        if (oauthCandidate)
        {
            account.OAuthRefreshToken = credential;
            account.ClientId = suppliedClientId;
            account.CredentialKind = MailCredentialKind.OAuth2RefreshToken;
            account.CredentialScope = MailCredentialScope.Unknown;
            account.CredentialDetectionStatus = provider.Kind == MailProviderKind.Custom
                ? "OAuthConfigurationRequired"
                : string.IsNullOrWhiteSpace(suppliedClientId)
                ? "OAuthConfigurationRequired"
                : "PendingVerification";
            account.Password = null;
            return;
        }

        account.Password = _encryption.Encrypt(provider.NormalizeAppPassword(credential));
        // For a custom domain, a two-column upstream row cannot tell us whether
        // the opaque value is a shared mailbox password or a provider-specific
        // token. Keep it Unknown until IMAP/SMTP authentication proves the scope.
        account.CredentialKind = provider.Kind is MailProviderKind.Custom or MailProviderKind.Outlook
            ? MailCredentialKind.Unknown
            : MailCredentialKind.SharedMailPassword;
        account.CredentialScope = MailCredentialScope.Unknown;
        account.OAuthRefreshToken = null;
        account.ClientId = null;
    }

    private IMailProviderModule ResolveProvider(string email, string? domain)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var suppliedDomain = domain.Trim().TrimStart('@').ToLowerInvariant();
            var actualDomain = new MailAddress(email).Host.ToLowerInvariant();
            if (!string.Equals(suppliedDomain, actualDomain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("接口中的域名与邮箱地址不一致。");
        }

        return _registry.Detect(email);
    }

    private static string NormalizeEmail(string value)
    {
        try
        {
            var address = new MailAddress(value.Trim());
            if (!address.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FormatException();
            return address.Address;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("邮箱地址格式不正确。");
        }
    }

    private static bool LooksLikeGoogleAppPassword(string value)
    {
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        return compact.Length == 16 && compact.All(char.IsLetterOrDigit);
    }

    private static bool LooksLikeOAuthRefreshToken(string value)
        => value.StartsWith("1//", StringComparison.Ordinal)
            || value.StartsWith("ya29.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("M.", StringComparison.Ordinal);
}
