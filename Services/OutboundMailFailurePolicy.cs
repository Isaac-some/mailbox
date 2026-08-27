namespace MailArchiver.Services;

public static class OutboundMailFailurePolicy
{
    public static string ToSafeMessage(Exception exception)
        => exception switch
        {
            OutlookGraphAuthorizationException graphAuthorization => graphAuthorization.Message,
            OutlookGraphMailException graphMail => graphMail.UserMessage,
            MsaOAuthTokenException oauth when (int)oauth.StatusCode is 429 or >= 500
                => "微软授权服务暂时不可用，请稍后重试。",
            MsaOAuthTokenException oauth
                => $"微软授权服务返回错误（HTTP {(int)oauth.StatusCode}，{oauth.ErrorCode}）。",
            MailKit.Security.AuthenticationException => "发件认证失败，请检查账号授权或应用密码。",
            HttpRequestException => "连接发件服务失败，请检查网络后重试。",
            TimeoutException => "连接发件服务超时。",
            _ => "发送失败，邮件尚未确认发出。请检查账号授权和网络后重试。"
        };
}
