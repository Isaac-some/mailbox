using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MailArchiver.Models;
namespace MailArchiver.Services.Providers.Outlook;

public sealed class OutlookIncomingAuthorizationException(Exception graph, Exception imap)
    : Exception("Outlook 收件通道验证未通过，请检查账号的收件权限或重新授权。", new AggregateException(graph, imap));

/// <summary>Remembers a working IMAP fallback briefly; a credential change always tries Graph again.</summary>
public sealed class OutlookIncomingRoutePolicy
{
    private readonly ConcurrentDictionary<int, (string Fingerprint, DateTime Until)> _imap = new();
    private readonly ConcurrentDictionary<int, DateTime> _retryAfter = new();
    private static string Fingerprint(MailAccount account) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{account.EmailAddress}\n{account.ClientId}\n{account.ClientSecret}\n{account.OAuthRefreshToken}\n{account.Password}")));
    public bool ShouldTryGraph(MailAccount account) => !string.IsNullOrWhiteSpace(account.OAuthRefreshToken)
        && (!_imap.TryGetValue(account.Id, out var route) || route.Until <= DateTime.UtcNow || route.Fingerprint != Fingerprint(account));
    public void RememberImap(MailAccount account) => _imap[account.Id] = (Fingerprint(account), DateTime.UtcNow.AddMinutes(15));
    public void RememberGraph(MailAccount account) => _imap.TryRemove(account.Id, out _);
    public async Task<T> ExecuteAsync<T>(MailAccount account, Func<Task<T>> graph, Func<Task<T>> imap)
    {
        if (_retryAfter.TryGetValue(account.Id, out var until) && until > DateTime.UtcNow)
            throw new OutlookGraphReadException(429, until - DateTime.UtcNow);
        if (!ShouldTryGraph(account))
        {
            try { return await imap(); }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(account.OAuthRefreshToken)
                && ex is not OperationCanceledException && !IsTransient(ex))
            { RememberGraph(account); }
        }
        Exception graphFailure;
        try { var value = await graph(); RememberGraph(account); return value; }
        catch (OutlookGraphReadException ex) when (ex.StatusCode == 429)
        { _retryAfter[account.Id] = DateTime.UtcNow.Add(ex.RetryAfter ?? TimeSpan.FromSeconds(60)); throw; }
        catch (Exception ex) when (CanFallback(ex)) { graphFailure = ex; }
        try { var value = await imap(); RememberImap(account); return value; }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsTransient(ex))
        { throw new OutlookIncomingAuthorizationException(graphFailure, ex); }
    }
    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException
        or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException
        || ex.InnerException is not null && IsTransient(ex.InnerException)
        || ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsTransient);
    public static bool CanFallback(Exception exception) => exception is OutlookGraphReadException { StatusCode: 400 or 401 or 403 or 404 }
        || exception is MsaOAuthTokenException { IsAuthorizationFailure: true };
}

public sealed class OutlookGraphReadException(int statusCode, TimeSpan? retryAfter = null)
    : HttpRequestException(statusCode == 429 ? "Outlook 收件已限流，请稍后重试。" : "Outlook Graph 收件请求未通过。",
        null, (System.Net.HttpStatusCode)statusCode)
{
    public new int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
