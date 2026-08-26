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
            localDataDirectory: "/Users/test/Library/Application Support/KouziMailAssistant");

        Assert.Equal(
            "/Users/test/Library/Application Support/KouziMailAssistant",
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

        Assert.Contains("KOUZI_DATA_DIRECTORY", exception.Message);
    }
}
