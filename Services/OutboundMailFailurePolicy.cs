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
            MailKit.Net.Smtp.SmtpCommandException smtp when smtp.StatusCode is MailKit.Net.Smtp.SmtpStatusCode.AuthenticationInvalidCredentials
                or MailKit.Net.Smtp.SmtpStatusCode.AuthenticationRequired
                => "GMX SMTP 拒绝了账号认证，请确认已启用 POP3/IMAP、使用应用专用密码，并重新刷新账号。",
            MailKit.Net.Smtp.SmtpCommandException smtp when (int)smtp.StatusCode is >= 400 and < 500
                => "发件服务器暂时拒绝了 SMTP 请求，请稍后重试并检查账号是否被限制。",
            MailKit.Net.Smtp.SmtpCommandException => "发件服务器拒绝了 SMTP 请求，请检查邮箱服务设置。",
            MailKit.Net.Smtp.SmtpProtocolException => "发件服务器的 SMTP 会话未完成，请检查网络代理或邮箱服务设置。",
            MailKit.Security.SslHandshakeException => "发件服务器 TLS 握手失败，请检查当前网络代理或 VPN 路由。",
            HttpRequestException => "连接发件服务失败，请检查网络后重试。",
            IOException or System.Net.Sockets.SocketException => "连接发件服务失败，请检查网络、代理或 VPN 后重试。",
            TimeoutException => "连接发件服务超时。",
            _ => "发送失败，邮件尚未确认发出。请检查账号授权和网络后重试。"
        };
}
