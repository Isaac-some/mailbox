using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailCredentialFallbackTests
{
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
