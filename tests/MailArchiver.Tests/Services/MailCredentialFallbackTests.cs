using MailArchiver.Models;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailCredentialFallbackTests
{
    [Theory]
    [InlineData(MailProviderKind.Gmail, MailCredentialPreference.AppPasswordFirst)]
    [InlineData(MailProviderKind.Yahoo, MailCredentialPreference.AppPasswordFirst)]
    [InlineData(MailProviderKind.Gmx, MailCredentialPreference.AppPasswordFirst)]
    [InlineData(MailProviderKind.Outlook, MailCredentialPreference.OAuthFirst)]
    public void Provider_policy_has_the_expected_primary_credential(
        MailProviderKind provider,
        MailCredentialPreference expected)
    {
        Assert.Equal(expected, MailProviderCredentialPolicy.For(provider));
    }

    [Theory]
    [InlineData(MailAuthenticationMethod.Password, MailCredentialPreference.AppPasswordFirst)]
    [InlineData(MailAuthenticationMethod.OAuth2, MailCredentialPreference.OAuthFirst)]
    public void Remembered_success_overrides_the_provider_default(
        MailAuthenticationMethod remembered,
        MailCredentialPreference expected)
    {
        Assert.Equal(expected, MailProviderCredentialPolicy.For(MailProviderKind.Outlook, remembered));
    }

    [Fact]
    public async Task App_password_success_does_not_try_OAuth()
    {
        var attempts = new List<string>();

        var used = await MailCredentialFallback.AuthenticateAsync(
            hasOAuth: true,
            hasPassword: true,
            authenticateOAuth: () =>
            {
                attempts.Add("oauth");
                return Task.CompletedTask;
            },
            authenticatePassword: () =>
            {
                attempts.Add("password");
                return Task.CompletedTask;
            },
            preference: MailCredentialPreference.AppPasswordFirst);

        Assert.Equal(["password"], attempts);
        Assert.Equal(MailAuthenticationMethod.Password, used);
    }

    [Fact]
    public async Task App_password_failure_tries_the_independent_OAuth_credentials()
    {
        var attempts = new List<string>();

        var used = await MailCredentialFallback.AuthenticateAsync(
            hasOAuth: true,
            hasPassword: true,
            authenticateOAuth: () =>
            {
                attempts.Add("oauth");
                return Task.CompletedTask;
            },
            authenticatePassword: () =>
            {
                attempts.Add("password");
                throw new InvalidOperationException("revoked app password");
            },
            preference: MailCredentialPreference.AppPasswordFirst);

        Assert.Equal(["password", "oauth"], attempts);
        Assert.Equal(MailAuthenticationMethod.OAuth2, used);
    }

    [Fact]
    public async Task OAuth_failure_tries_the_independent_app_password()
    {
        var attempts = new List<string>();

        await MailCredentialFallback.AuthenticateAsync(
            hasOAuth: true,
            hasPassword: true,
            authenticateOAuth: () =>
            {
                attempts.Add("oauth");
                throw new InvalidOperationException("revoked token");
            },
            authenticatePassword: () =>
            {
                attempts.Add("password");
                return Task.CompletedTask;
            });

        Assert.Equal(["oauth", "password"], attempts);
    }

    [Fact]
    public async Task OAuth_success_does_not_try_the_app_password()
    {
        var attempts = new List<string>();

        await MailCredentialFallback.AuthenticateAsync(
            hasOAuth: true,
            hasPassword: true,
            authenticateOAuth: () =>
            {
                attempts.Add("oauth");
                return Task.CompletedTask;
            },
            authenticatePassword: () =>
            {
                attempts.Add("password");
                return Task.CompletedTask;
            });

        Assert.Equal(["oauth"], attempts);
    }

    [Fact]
    public async Task OAuth_failure_is_not_hidden_when_no_password_exists()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailCredentialFallback.AuthenticateAsync(
                hasOAuth: true,
                hasPassword: false,
                authenticateOAuth: () => throw new InvalidOperationException("revoked token"),
                authenticatePassword: () => Task.CompletedTask));

        Assert.Equal("revoked token", exception.Message);
    }
}
