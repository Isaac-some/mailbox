namespace MailArchiver.Services;

internal static class MailCredentialFallback
{
    public static async Task AuthenticateAsync(
        bool hasOAuth,
        bool hasPassword,
        Func<Task> authenticateOAuth,
        Func<Task> authenticatePassword,
        CancellationToken cancellationToken = default)
    {
        if (hasOAuth)
        {
            try
            {
                await authenticateOAuth();
                return;
            }
            catch (Exception) when (hasPassword && !cancellationToken.IsCancellationRequested)
            {
                // Continue to the independently supplied app password.
            }
        }

        await authenticatePassword();
    }
}
