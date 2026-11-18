using System.Net;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Providers.Outlook;
namespace MailArchiver.Tests.Services;

public sealed class OutlookGraphReceivingTests
{
    private static MailAccount Account() => new() { Id = 81, Name = "Test", EmailAddress = "alias@outlook.com", Provider = ProviderType.MSA,
        MailProviderKind = MailProviderKind.Outlook, ClientId = "client", OAuthRefreshToken = "refresh" };
    private const string Mail = "{\"value\":[{\"id\":\"message\",\"subject\":\"hello\",\"body\":{\"contentType\":\"text\",\"content\":\"real body\"},\"hasAttachments\":false}]}";
    [Fact]
    public async Task Verification_succeeds_through_real_Graph_client_without_touching_IMAP_or_user_profile()
    {
        var http = new Responses(_ => (200, Mail)); var tokens = new Tokens();
        var reader = new OutlookGraphMailboxClient(tokens, http); var account = Account();
        var verifier = new MailCredentialVerifier(null!, null!, null!, outlookReader: reader, outlookRoutes: new());
        await verifier.VerifyAsync(account);
        Assert.Equal(MailCredentialScope.Graph, account.CredentialScope);
        Assert.Equal("IncomingVerified", account.CredentialDetectionStatus);
        Assert.Single(http.Uris); Assert.Contains("/me/mailFolders/inbox/messages", http.Uris[0].AbsolutePath);
        Assert.DoesNotContain("/users/", http.Uris[0].AbsolutePath);
        Assert.Equal(1, tokens.ReadCalls); Assert.Equal(0, tokens.OtherCalls);
    }
    [Theory]
    [InlineData(7, 10)] [InlineData(30, 30)]
    public async Task Graph_fetches_a_bounded_batch_including_bodies_and_the_requested_date_range(int days, int limit)
    {
        var http = new Responses(_ => (200, Mail)); var reader = new OutlookGraphMailboxClient(new Tokens(), http);
        var result = await reader.ReadAsync(Account(), "junkemail", days, limit);
        Assert.Equal("real body", Assert.Single(result).Body!.Content);
        var query = Uri.UnescapeDataString(Assert.Single(http.Uris).Query);
        Assert.Contains("$top=" + limit, query); Assert.Contains("body", query); Assert.Contains("hasAttachments", query);
        Assert.Contains("receivedDateTime ge ", query); Assert.Contains(DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd"), query);
    }
    [Fact]
    public async Task Expired_Graph_access_is_refreshed_once_then_retried()
    {
        var http = new Responses(n => n == 1 ? (401, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"expired\"}}") : (200, Mail));
        var tokens = new Tokens(); await new OutlookGraphMailboxClient(tokens, http).VerifyAsync(Account());
        Assert.Equal(1, tokens.ForcedRefreshes); Assert.Equal(2, http.Uris.Count); Assert.Equal(0, tokens.OtherCalls);
    }
    [Fact]
    public async Task Successful_IMAP_fallback_is_remembered_and_credential_changes_try_Graph_again()
    {
        var routes = new OutlookIncomingRoutePolicy(); var account = Account(); var graph = 0; var imap = 0;
        Task<bool> Graph() { graph++; throw new OutlookGraphReadException(403); }
        Task<bool> Imap() { imap++; return Task.FromResult(true); }
        await routes.ExecuteAsync(account, Graph, Imap); await routes.ExecuteAsync(account, Graph, Imap);
        Assert.Equal(1, graph); Assert.Equal(2, imap);
        account.OAuthRefreshToken = "replacement";
        await routes.ExecuteAsync(account, Graph, Imap); Assert.Equal(2, graph);
    }
    [Theory]
    [InlineData(429)] [InlineData(500)] [InlineData(503)]
    public async Task Throttling_and_service_failures_never_trigger_another_login_route(int status)
    {
        var imap = 0; var error = new OutlookGraphReadException(status, TimeSpan.FromSeconds(60));
        var caught = await Assert.ThrowsAsync<OutlookGraphReadException>(() => new OutlookIncomingRoutePolicy().ExecuteAsync(Account(),
            () => Task.FromException<bool>(error), () => { imap++; return Task.FromResult(true); }));
        Assert.Same(error, caught); Assert.Equal(0, imap);
        if (status == 429) Assert.Equal(MailFailureCode.RateLimited, MailConnectionFailurePolicy.Classify(error).Code);
    }
    [Fact]
    public async Task Network_failure_never_triggers_IMAP_password_attempts()
    {
        var imap = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => new OutlookIncomingRoutePolicy().ExecuteAsync(Account(),
            () => Task.FromException<bool>(new HttpRequestException("network")), () => { imap++; return Task.FromResult(true); }));
        Assert.Equal(0, imap);
    }
    [Fact]
    public async Task Both_receiving_routes_rejected_do_not_claim_the_credential_itself_is_invalid()
    {
        var error = await Assert.ThrowsAsync<OutlookIncomingAuthorizationException>(() => new OutlookIncomingRoutePolicy().ExecuteAsync(Account(),
            () => Task.FromException<bool>(new OutlookGraphReadException(403)),
            () => Task.FromException<bool>(new MailKit.Security.AuthenticationException("IMAP unavailable"))));
        var failure = MailConnectionFailurePolicy.Classify(error);
        Assert.Equal(MailFailureCode.ReceivingAuthorizationRejected, failure.Code);
        Assert.DoesNotContain("授权码无效", failure.Message);
    }
    [Fact]
    public async Task Folder_pagination_never_forwards_credentials_to_a_foreign_host()
    {
        var http = new Responses(_ => (200, "{\"value\":[],\"@odata.nextLink\":\"https://untrusted.example/steal\"}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutlookGraphMailboxClient(new Tokens(), http).GetFoldersAsync(Account()));
        Assert.Single(http.Uris);
    }
    [Fact]
    public async Task A_failed_cached_IMAP_route_can_recover_through_Graph()
    {
        var account = Account(); var routes = new OutlookIncomingRoutePolicy(); routes.RememberImap(account);
        Assert.True(await routes.ExecuteAsync(account, () => Task.FromResult(true),
            () => Task.FromException<bool>(new MailKit.Security.AuthenticationException("IMAP rejected"))));
        Assert.True(routes.ShouldTryGraph(account));
    }
    [Fact]
    public async Task Retry_after_blocks_repeated_requests_only_for_the_limited_mailbox()
    {
        var account = Account(); var routes = new OutlookIncomingRoutePolicy(); var calls = 0;
        Task<bool> Limited() { calls++; throw new OutlookGraphReadException(429, TimeSpan.FromMinutes(1)); }
        await Assert.ThrowsAsync<OutlookGraphReadException>(() => routes.ExecuteAsync(account, Limited, () => Task.FromResult(true)));
        await Assert.ThrowsAsync<OutlookGraphReadException>(() => routes.ExecuteAsync(account, Limited, () => Task.FromResult(true)));
        Assert.Equal(1, calls);
        var other = Account(); other.Id++;
        Assert.True(await routes.ExecuteAsync(other, () => Task.FromResult(true), () => Task.FromResult(false)));
    }
    [Fact]
    public async Task Message_pagination_preserves_bodies_and_stops_at_the_requested_limit()
    {
        var http = new Responses(n => (200, n == 1
            ? "{\"value\":[{\"id\":\"one\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$skip=1\"}"
            : "{\"value\":[{\"id\":\"two\",\"body\":{\"contentType\":\"text\",\"content\":\"second body\"}},{\"id\":\"extra\"}]}"));
        var result = await new OutlookGraphMailboxClient(new Tokens(), http).ReadAsync(Account(), "inbox", 7, 2);
        Assert.Equal(2, result.Count); Assert.Equal("second body", result[1].Body!.Content); Assert.Equal(2, http.Uris.Count);
    }
    private sealed class Tokens : IMsaTokenManager
    {
        public int ReadCalls, ForcedRefreshes, OtherCalls;
        public Task<MsaAccessToken> GetGraphReadAccessTokenAsync(MailAccount a, bool forceRefresh = false, CancellationToken cancellationToken = default)
        { ReadCalls++; if (forceRefresh) ForcedRefreshes++; return Task.FromResult(new MsaAccessToken(a.EmailAddress, "read-access")); }
        private Task<MsaAccessToken> Other() { OtherCalls++; throw new Exception("Must not use IMAP, SMTP or Graph sending tokens for receiving."); }
        public Task<MsaAccessToken> GetAccessTokenAsync(MailAccount a, bool forceRefresh = false, CancellationToken cancellationToken = default) => Other();
        public Task<MsaAccessToken> GetGraphAccessTokenAsync(MailAccount a, bool forceRefresh = false, CancellationToken cancellationToken = default) => Other();
        public Task<MsaAccessToken> GetSmtpAccessTokenAsync(MailAccount a, bool forceRefresh = false, CancellationToken cancellationToken = default) => Other();
    }
    private sealed class Responses(Func<int, (int Status, string Body)> respond) : INetworkHttpClientFactory
    {
        public List<Uri> Uris { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, respond));
        private sealed class Handler(Responses owner, Func<int, (int Status, string Body)> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal("read-access", request.Headers.Authorization?.Parameter);
                owner.Uris.Add(request.RequestUri!); var (status, body) = respond(owner.Uris.Count);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
