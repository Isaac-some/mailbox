using MailArchiver.Services;
using MailKit.Security;
using System.Net.Sockets;
using MailArchiver.Models;

namespace MailArchiver.Tests.Services;

public class MailConnectionFailurePolicyTests
{
    [Fact]
    public void Authentication_rejection_is_reported_as_invalid_authorization_code()
    {
        var failure = MailConnectionFailurePolicy.Classify(
            new AuthenticationException("provider rejected credentials"));

        Assert.Equal(MailFailureCode.AuthenticationRejected, failure.Code);
        Assert.StartsWith("授权码无效", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_login_rate_limit_is_not_reported_as_an_invalid_authorization_code()
    {
        var failure = MailConnectionFailurePolicy.Classify(
            new AuthenticationException("LOGIN Rate limit hit."));

        Assert.Equal(MailFailureCode.RateLimited, failure.Code);
    }

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public void Network_failures_are_reported_as_connection_failures(Exception exception)
    {
        var failure = MailConnectionFailurePolicy.Classify(exception);

        Assert.Equal(MailFailureCode.TargetUnavailable, failure.Code);
        Assert.StartsWith("连接失败", failure.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Exception> ConnectionFailures => new()
    {
        new SocketException((int)SocketError.HostNotFound),
        new TimeoutException("timed out"),
        new IOException("transport failed"),
        new AggregateException(new HttpRequestException("network unavailable"))
    };

    [Fact]
    public void Gmail_format_failure_is_not_misreported_as_network_failure()
    {
        var failure = MailConnectionFailurePolicy.Classify(
            new MailCredentialFormatException("invalid length"));
        Assert.Equal(MailFailureCode.GmailCredentialFormatInvalid, failure.Code);
        Assert.Equal(MailFailureStage.CredentialValidation, failure.Stage);
        Assert.Contains("16 位", failure.Message);
    }

    [Fact]
    public void Raw_exception_message_never_reaches_sync_status()
    {
        Assert.DoesNotContain("secret", MailConnectionFailurePolicy.SafeMessage(null));
    }

    [Fact]
    public void Wrapped_authentication_error_preserves_the_specific_cause()
    {
        var failure = MailConnectionFailurePolicy.Classify(
            new InvalidOperationException("validation failed", new AuthenticationException("secret")));
        Assert.Equal(MailFailureCode.AuthenticationRejected, failure.Code);
        Assert.DoesNotContain("secret", failure.Message);
    }
}
