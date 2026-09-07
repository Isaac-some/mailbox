namespace MailArchiver.Services;

public static class LocalAppStoragePathPolicy
{
    public static string ResolveWritableRoot(string contentRootPath)
    {
        return ResolveWritableRoot(
            contentRootPath,
            string.Equals(
                Environment.GetEnvironmentVariable("MAIL_ASSISTANT_LOCAL_APP"),
                "1",
                StringComparison.Ordinal),
            Environment.GetEnvironmentVariable("MAIL_ASSISTANT_DATA_DIRECTORY"));
    }

    public static string ResolveWritableRoot(
        string contentRootPath,
        bool isLocalApp,
        string? localDataDirectory)
    {
        if (!isLocalApp)
        {
            return contentRootPath;
        }

        if (string.IsNullOrWhiteSpace(localDataDirectory))
        {
            throw new InvalidOperationException(
                "MAIL_ASSISTANT_DATA_DIRECTORY must be set when MAIL_ASSISTANT_LOCAL_APP=1.");
        }

        return localDataDirectory;
    }
}
