using MailArchiver.Models;

namespace MailArchiver.Services;

public enum MailCredentialPreference
{
    OAuthFirst,
    AppPasswordFirst
}

public static class MailProviderCredentialPolicy
{
    public static MailCredentialPreference For(
        MailProviderKind provider,
        MailAuthenticationMethod remembered = MailAuthenticationMethod.Unknown)
        => remembered switch
        {
            MailAuthenticationMethod.OAuth2 => MailCredentialPreference.OAuthFirst,
            MailAuthenticationMethod.Password => MailCredentialPreference.AppPasswordFirst,
            _ => provider switch
            {
                MailProviderKind.Outlook => MailCredentialPreference.OAuthFirst,
                MailProviderKind.Gmail or MailProviderKind.Yahoo or MailProviderKind.Gmx or MailProviderKind.Custom
                    => MailCredentialPreference.AppPasswordFirst,
                _ => throw new NotSupportedException($"不支持邮箱服务商 {provider}。")
            }
        };
}

internal static class MailCredentialFallback
{
    public static async Task<MailAuthenticationMethod> AuthenticateAsync(
        bool hasOAuth,
        bool hasPassword,
        Func<Task> authenticateOAuth,
        Func<Task> authenticatePassword,
        MailCredentialPreference preference = MailCredentialPreference.OAuthFirst,
        CancellationToken cancellationToken = default)
    {
        if (!hasOAuth && !hasPassword)
            throw new InvalidOperationException("邮箱账号没有可用的认证凭据。");

        var failures = new List<Exception>();
        var tryPasswordFirst = preference == MailCredentialPreference.AppPasswordFirst;
        foreach (var attemptPassword in tryPasswordFirst ? new[] { true, false } : new[] { false, true })
        {
            if (attemptPassword ? !hasPassword : !hasOAuth)
                continue;
            try
            {
                if (attemptPassword)
                    await authenticatePassword();
                else
                    await authenticateOAuth();
                return attemptPassword
                    ? MailAuthenticationMethod.Password
                    : MailAuthenticationMethod.OAuth2;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            throw failures[0];
        throw new AggregateException("邮箱 OAuth 和应用专用密码认证均失败。", failures);
    }
}
