using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace MailArchiver.Tests.Services;

public class LocalAccessServiceTests
{
    [Fact]
    public async Task Bundled_account_requires_matching_password_and_active_state()
    {
        var service = CreateService([
            new LocalAccessAccount { Username = "reader", Password = "correct", IsActive = true },
            new LocalAccessAccount { Username = "disabled", Password = "correct", IsActive = false }
        ]);

        Assert.True((await service.ValidateAsync("reader", "correct")).Succeeded);
        Assert.False((await service.ValidateAsync("reader", "wrong")).Succeeded);
        Assert.False((await service.ValidateAsync("disabled", "correct")).Succeeded);
        Assert.True(await service.IsActiveAsync("reader"));
        Assert.False(await service.IsActiveAsync("disabled"));
    }

    [Fact]
    public async Task Remote_manifest_controls_active_state_without_receiving_password_hashes()
    {
        var options = new LocalAccessOptions
        {
            Enabled = true,
            ManifestUrl = "https://example.test/local-access.json",
            Accounts =
            [
                new LocalAccessAccount { Username = "reader", Password = "reader-pass", IsActive = true },
                new LocalAccessAccount { Username = "admin", Password = "admin-pass", IsActive = true, IsAdmin = true }
            ]
        };
        var service = new LocalAccessService(
            new ManifestHttpClientFactory("""{"enabled":true,"accounts":[{"username":"reader","isActive":true}]}"""),
            Options.Create(options),
            NullLogger<LocalAccessService>.Instance);

        Assert.True((await service.ValidateAsync("reader", "reader-pass")).Succeeded);
        Assert.False((await service.ValidateAsync("admin", "admin-pass")).Succeeded);
    }

    private static LocalAccessService CreateService(List<LocalAccessAccount> accounts)
        => new(
            new UnusedHttpClientFactory(),
            Options.Create(new LocalAccessOptions { Enabled = true, Accounts = accounts }),
            NullLogger<LocalAccessService>.Instance);

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No manifest URL was configured.");
    }

    private sealed class ManifestHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ManifestHandler(json));
    }

    private sealed class ManifestHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
