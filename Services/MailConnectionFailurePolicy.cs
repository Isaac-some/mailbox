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
                SafeMessage(nameof(MailFailureCode.ProxyUnavailable)),
                MailFailureStage.ProxyHandshake, exception);

        if (failures.Any(failure => failure is AuthenticationException))
            return Failure(MailFailureCode.AuthenticationRejected,
                "授权码无效，请检查邮箱与授权码是否匹配。",
                MailFailureStage.Authentication, exception);

        if (failures.Any(failure => failure is SslHandshakeException
            or System.Security.Authentication.AuthenticationException))
            return Failure(MailFailureCode.TlsFailed,
                SafeMessage(nameof(MailFailureCode.TlsFailed)),
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
                MailFailureCode.ProxyUnavailable => "当前代理节点拒绝连接该邮箱域名的收发端口，请切换节点后重试，或联系节点提供方检查端口限制。",
                MailFailureCode.TargetUnavailable => "连接失败，请检查网络、VPN、代理或邮箱服务是否可访问。",
                MailFailureCode.TlsFailed => "TLS 安全连接失败：当前 VPN/代理节点可能无法连接该邮箱域名的收发端口，请切换节点后重试；若仍失败，请检查域名连接设置中的服务器地址、端口、加密方式及证书。",
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
