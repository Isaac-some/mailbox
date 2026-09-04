using MailKit.Security;
using System.Net.Sockets;

namespace MailArchiver.Services;

public static class MailConnectionFailurePolicy
{
    public static string ToUserMessage(Exception exception)
    {
        var failures = Flatten(exception).ToArray();
        if (failures.Any(IsRateLimit))
            return "邮箱服务暂时限制登录，请稍后重试。";

        if (failures.Any(failure => failure is AuthenticationException))
            return "授权码无效，请检查邮箱与授权码是否匹配。";

        if (failures.Any(IsConnectionFailure))
            return "连接失败，请检查网络、VPN、代理或邮箱服务是否可访问。";

        return "邮箱同步失败，请检查邮箱设置后重试。";
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var nested in Flatten(inner))
                    yield return nested;
            }
            yield break;
        }

        if (exception.InnerException is not null)
        {
            foreach (var nested in Flatten(exception.InnerException))
                yield return nested;
        }
    }

    private static bool IsConnectionFailure(Exception exception)
        => exception is SocketException
            or TimeoutException
            or OperationCanceledException
            or IOException
            or HttpRequestException
            or SslHandshakeException;

    private static bool IsRateLimit(Exception exception)
        => exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("too many", StringComparison.OrdinalIgnoreCase);
}
