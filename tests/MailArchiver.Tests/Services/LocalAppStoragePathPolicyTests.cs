using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class LocalAppStoragePathPolicyTests
{
    [Fact]
    public void LocalApp_UsesConfiguredDataDirectory()
    {
        var result = LocalAppStoragePathPolicy.ResolveWritableRoot(
            contentRootPath: "/read-only/app/server",
            isLocalApp: true,
            localDataDirectory: "/Users/test/Library/Application Support/MailAssistant");

        Assert.Equal(
            "/Users/test/Library/Application Support/MailAssistant",
            result);
    }

    [Fact]
    public void ServerDeployment_PreservesContentRoot()
    {
        var result = LocalAppStoragePathPolicy.ResolveWritableRoot(
            contentRootPath: "/app",
            isLocalApp: false,
            localDataDirectory: "/ignored/local-data");

        Assert.Equal("/app", result);
    }

    [Fact]
    public void LocalApp_RequiresConfiguredDataDirectory()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalAppStoragePathPolicy.ResolveWritableRoot(
                contentRootPath: "/read-only/app/server",
                isLocalApp: true,
                localDataDirectory: " "));

        Assert.Contains("MAIL_ASSISTANT_DATA_DIRECTORY", exception.Message);
    }
}
