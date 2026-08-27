using System.Net;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class OutboundMailFailurePolicyTests
{
    [Fact]
    public void Graph_permission_failure_is_not_hidden_as_a_network_error()
    {
        var failure = new OutlookGraphMailException(
            HttpStatusCode.Forbidden,
            "ErrorAccessDenied",
            "Outlook Graph 发件授权失败，请确认 Refresh Token 包含 Mail.Send 权限。",
            "technical detail");

        Assert.Equal(failure.UserMessage, OutboundMailFailurePolicy.ToSafeMessage(failure));
    }

    [Fact]
    public void Network_failure_is_not_reported_as_missing_Graph_permission()
    {
        var message = OutboundMailFailurePolicy.ToSafeMessage(
            new HttpRequestException("DNS detail"));

        Assert.Contains("网络", message);
        Assert.DoesNotContain("Mail.Send", message);
    }
}
