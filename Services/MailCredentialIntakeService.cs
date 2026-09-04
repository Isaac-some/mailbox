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
/// Stores the upstream four-field contract without guessing the credential type.
/// The same opaque value is made available to password and refresh-token routes;
/// only a real provider connection is allowed to choose and remember a route.
/// </summary>
public sealed class MailCredentialIntakeService
{
    private readonly MailArchiverDbContext _context;
    private readonly IMailProviderRegistry _registry;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IMailCredentialVerifier _verifier;

    public MailCredentialIntakeService(
        MailArchiverDbContext context,
        IMailProviderRegistry registry,
        ICredentialEncryptionService encryption,
        IMailCredentialVerifier verifier)
    {
        _context = context;
        _registry = registry;
        _encryption = encryption;
        _verifier = verifier;
    }

    public async Task<MailCredentialIntakeResult> UpsertAsync(
        int userId,
        MailCredentialIntake input,
        bool enabled,
        CancellationToken cancellationToken = default,
        bool verifyCredential = true)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));

        var email = NormalizeEmail(input.Email);
        var credential = MailCredentialInputPolicy.NormalizeAndValidate(input.Credential);

        var provider = _registry.Detect(email);
        var existing = await _context.MailAccounts
            .Include(a => a.UserMailAccounts)
            .FirstOrDefaultAsync(a => a.EmailAddress.ToLower() == email.ToLower(), cancellationToken);
        if (existing is not null && !existing.UserMailAccounts.Any(link => link.UserId == userId))
            throw new InvalidOperationException("该邮箱已属于其他用户，当前接口不能跨用户覆盖凭据。");

        var created = existing is null;
        // Validate a detached copy with no database ID. Token refreshes must not
        // load or overwrite the existing row until the new login has succeeded.
        var account = existing is not null
            ? (MailAccount)_context.Entry(existing).CurrentValues.ToObject()
            : new MailAccount
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

        var domain = NormalizeDomain(input.Domain);
        // The standard export repeats the IMAP code in its 2FA/ClientId column
        // for Yahoo and GMX. Only Outlook's OAuth flow consumes that field.
        var clientId = provider.Kind == MailProviderKind.Outlook && !string.IsNullOrWhiteSpace(input.ClientId)
            ? input.ClientId.Trim()
            : null;
        var unchanged = !created
            && string.Equals(account.ImportedDomain, domain, StringComparison.Ordinal)
            && string.Equals(account.ClientId, clientId, StringComparison.Ordinal)
            && HasSameImportedCredential(account, credential);

        account.Id = 0;

        account.EmailAddress = email;
        account.ImportedDomain = domain;
        provider.PrepareAccount(account);
        account.IsEnabled = enabled;
        if (!unchanged)
        {
            ApplyCredential(account, credential, clientId);
            account.CredentialLastCheckedAt = null;
            account.CredentialKind = MailCredentialKind.Unknown;
            account.CredentialScope = MailCredentialScope.Unknown;
            account.CredentialDetectionStatus = "PendingVerification";
            account.PreferredIncomingAuth = MailAuthenticationMethod.Unknown;
            account.PreferredOutgoingAuth = MailAuthenticationMethod.Unknown;
        }
        else
        {
            // Normalize legacy input without replacing a provider-rotated token.
            account.Password = _encryption.Encrypt(credential);
        }

        if (verifyCredential)
            await _verifier.VerifyAsync(account, cancellationToken);

        if (existing is not null)
        {
            account.Id = existing.Id;
            _context.Entry(existing).CurrentValues.SetValues(account);
            account = existing;
        }

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

    private bool HasSameImportedCredential(MailAccount account, string credential)
    {
        // Password retains the original opaque input. Refresh tokens can rotate
        // during authentication, so they must not be compared to upstream input.
        try
        {
            return !string.IsNullOrEmpty(account.Password)
                && string.Equals(MailCredentialInputPolicy.Normalize(_encryption.Decrypt(account.Password)), credential, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // A fresh import can repair an unreadable legacy credential.
            return false;
        }
    }

    private void ApplyCredential(MailAccount account, string rawCredential, string? clientId)
    {
        var credential = rawCredential;
        var suppliedClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        // Do not classify here. A Google app password, an ordinary IMAP/SMTP
        // password and an OAuth refresh token are all opaque strings at intake.
        account.Password = _encryption.Encrypt(credential);
        account.OAuthRefreshToken = credential;
        account.ClientId = suppliedClientId;

        // A duplicate row is a full four-field replacement, so no access token
        // or provider-specific OAuth metadata from the previous row may survive.
        account.ClientSecret = null;
        account.OAuthAccessToken = null;
        account.OAuthTokenExpiry = null;
        account.OAuthGrantedScopes = null;
        account.OAuthRedirectUri = null;
    }

    private static string? NormalizeDomain(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().TrimStart('@').ToLowerInvariant();

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

}
