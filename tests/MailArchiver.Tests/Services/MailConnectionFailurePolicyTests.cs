using MailArchiver.Services;
using MailKit.Security;
using System.Net.Sockets;

namespace MailArchiver.Tests.Services;

public class MailConnectionFailurePolicyTests
{
    [Fact]
    public void Authentication_rejection_is_reported_as_invalid_authorization_code()
    {
        var message = MailConnectionFailurePolicy.ToUserMessage(
            new AuthenticationException("provider rejected credentials"));

        Assert.StartsWith("授权码无效", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_login_rate_limit_is_not_reported_as_an_invalid_authorization_code()
    {
        var message = MailConnectionFailurePolicy.ToUserMessage(
            new AuthenticationException("LOGIN Rate limit hit."));

        Assert.Equal("邮箱服务暂时限制登录，请稍后重试。", message);
    }

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public void Network_failures_are_reported_as_connection_failures(Exception exception)
    {
        var message = MailConnectionFailurePolicy.ToUserMessage(exception);

        Assert.StartsWith("连接失败", message, StringComparison.Ordinal);
    }

    public static TheoryData<Exception> ConnectionFailures => new()
    {
        new SocketException((int)SocketError.HostNotFound),
        new TimeoutException("timed out"),
        new IOException("transport failed"),
        new AggregateException(new HttpRequestException("network unavailable"))
    };
}
