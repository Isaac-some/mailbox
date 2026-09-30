using MailKit.Security;
using MailKit.Net.Proxy;
using MailArchiver.Models;
using System.Net.Sockets;

namespace MailArchiver.Services;

public static class MailConnectionFailurePolicy
{
    public static MailFailure Classify(Exception exception)
    {
        var failures = Flatten(exception).ToArray();
        if (failures.Any(failure => failure is MailCredentialFormatException))
            return Failure(MailFailureCode.GmailCredentialFormatInvalid,
                "Gmail 应用专用密码去除空白和不可见字符后必须恰好是 16 位。",
                MailFailureStage.CredentialValidation, exception);

        if (failures.Any(IsRateLimit))
            return Failure(MailFailureCode.RateLimited,
                "当前同步已限流，请稍后重试。",
                MailFailureStage.RateLimit, exception);

        if (failures.Any(failure => failure is ProxyProtocolException))
            return Failure(MailFailureCode.ProxyUnavailable,
                "代理拒绝了邮箱目标连接，请检查代理是否允许该目标主机和端口。",
                MailFailureStage.ProxyHandshake, exception);

        if (failures.Any(failure => failure is AuthenticationException))
            return Failure(MailFailureCode.AuthenticationRejected,
                "授权码无效，请检查邮箱与授权码是否匹配。",
                MailFailureStage.Authentication, exception);

        if (failures.Any(failure => failure is SslHandshakeException
            or System.Security.Authentication.AuthenticationException))
            return Failure(MailFailureCode.TlsFailed,
                "TLS 安全连接失败，请检查代理或邮箱服务器证书。",
                MailFailureStage.Tls, exception);

        if (failures.Any(failure => failure is OperationCanceledException))
            return Failure(MailFailureCode.Cancelled,
                "同步任务已取消。",
                MailFailureStage.Cancelled, exception);

        if (failures.Any(IsConnectionFailure))
            return Failure(MailFailureCode.TargetUnavailable,
                "连接失败，请检查网络、VPN、代理或邮箱服务是否可访问。",
                MailFailureStage.TargetConnect, exception);

        if (failures.Any(IsProtocolFailure))
            return Failure(MailFailureCode.ProtocolFailed,
                "邮箱服务器协议响应异常，请稍后重试。",
                MailFailureStage.ProtocolGreeting, exception);

        return Failure(MailFailureCode.Unexpected,
            "邮箱同步失败，请检查邮箱设置后重试。",
            MailFailureStage.Unexpected, exception);
    }

    public static string SafeMessage(string? errorCode)
        => Enum.TryParse<MailFailureCode>(errorCode, out var code)
            ? code switch
            {
                MailFailureCode.GmailCredentialFormatInvalid => "Gmail 应用专用密码去除空白和不可见字符后必须恰好是 16 位。",
                MailFailureCode.AuthenticationRejected => "授权码无效，请检查邮箱与授权码是否匹配。",
                MailFailureCode.ProxyUnavailable => "代理拒绝了邮箱目标连接，请检查代理是否允许该目标主机和端口。",
                MailFailureCode.TargetUnavailable => "连接失败，请检查网络、VPN、代理或邮箱服务是否可访问。",
                MailFailureCode.TlsFailed => "TLS 安全连接失败，请检查代理或邮箱服务器证书。",
                MailFailureCode.ProtocolFailed => "邮箱服务器协议响应异常，请稍后重试。",
                MailFailureCode.RateLimited => "当前同步已限流，请稍后重试。",
                MailFailureCode.Cancelled => "同步任务已取消。",
                _ => "邮箱同步失败，请检查邮箱设置后重试。"
            }
            : "邮箱同步失败，请检查邮箱设置后重试。";

    private static MailFailure Failure(
        MailFailureCode code,
        string message,
        MailFailureStage stage,
        Exception exception)
        => new(code, message, stage, SafeExceptionCategory(exception));

    private static string SafeExceptionCategory(Exception exception)
    {
        var type = exception.GetType();
        return type.Namespace?.StartsWith("MailArchiver", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            ? type.Name
            : "ExternalException";
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
            or SslHandshakeException
            or ProxyProtocolException;

    private static bool IsRateLimit(Exception exception)
        => exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("too many", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocolFailure(Exception exception)
        => exception.GetType().Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true;
}
