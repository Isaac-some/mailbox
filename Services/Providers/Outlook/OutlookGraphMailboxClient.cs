using MailArchiver.Models;
using MailArchiver.Services.Providers.Imap;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
namespace MailArchiver.Services.Providers.Outlook;

public sealed record OutlookMailboxFolder(string Id, string Path, MailboxFolderCategory Category);
public interface IOutlookGraphMailboxClient
{
    Task VerifyAsync(MailAccount account, CancellationToken token = default);
    Task<List<Message>> ReadAsync(MailAccount account, string folderId, int days, int limit, CancellationToken token = default);
    Task<List<OutlookMailboxFolder>> GetFoldersAsync(MailAccount account, CancellationToken token = default);
    GraphServiceClient CreateClient(MailAccount account);
}

/// <summary>Personal Outlook uses /me with delegated mail consent, never the M365 app-only user/profile flow.</summary>
public sealed class OutlookGraphMailboxClient(IMsaTokenManager tokens, INetworkHttpClientFactory network) : IOutlookGraphMailboxClient
{
    public GraphServiceClient CreateClient(MailAccount account) => new(network.CreateClient("Graph"), new MailAuthentication(tokens, account));

    private async Task<T> RequestAsync<T>(MailAccount account, Func<GraphServiceClient, Task<T>> request, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var client = CreateClient(account);
            try { return await request(client); }
            catch (ApiException ex) when (ex.ResponseStatusCode == 401 && attempt == 0)
            { await tokens.GetGraphReadAccessTokenAsync(account, forceRefresh: true, cancellationToken: token); }
            catch (ApiException ex)
            {
                TimeSpan? delay = null;
                if (ex.ResponseHeaders?.TryGetValue("Retry-After", out var values) == true
                    && values.FirstOrDefault() is { } value)
                {
                    if (int.TryParse(value, out var seconds)) delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
                    else if (DateTimeOffset.TryParse(value, out var until)) delay = until > DateTimeOffset.UtcNow ? until - DateTimeOffset.UtcNow : TimeSpan.Zero;
                }
                throw new OutlookGraphReadException(ex.ResponseStatusCode, delay);
            }
        }
    }

    public async Task VerifyAsync(MailAccount account, CancellationToken token = default)
    {
        await RequestAsync(account, client => client.Me.MailFolders["inbox"].Messages.GetAsync(config =>
        {
            config.QueryParameters.Top = 1;
            // Mail.ReadBasic alone cannot return body. Verification tests the same
            // permission the mailbox screen requires, without downloading a profile.
            config.QueryParameters.Select = ["id", "body"];
        }, token), token);
    }

    public async Task<List<Message>> ReadAsync(MailAccount account, string folderId, int days, int limit, CancellationToken token = default)
    {
        limit = Math.Clamp(limit, 1, 30);
        var messages = new List<Message>();
        var seenPages = new HashSet<string>();
        string? next = null;
        do
        {
            var result = await RequestAsync(account, client =>
            {
                if (next is not null) return client.Me.MailFolders[folderId].Messages.WithUrl(next).GetAsync(cancellationToken: token);
                return client.Me.MailFolders[folderId].Messages.GetAsync(config =>
                {
                    config.QueryParameters.Top = limit;
                    config.QueryParameters.Orderby = ["receivedDateTime desc"];
                    config.QueryParameters.Filter = $"receivedDateTime ge {DateTime.UtcNow.AddDays(-days):yyyy-MM-ddTHH:mm:ssZ}";
                    config.QueryParameters.Select = ["id", "internetMessageId", "subject", "from", "toRecipients", "ccRecipients", "bccRecipients",
                        "receivedDateTime", "sentDateTime", "body", "bodyPreview", "hasAttachments", "internetMessageHeaders"];
                }, token);
            }, token);
            messages.AddRange((result?.Value ?? []).Take(limit - messages.Count));
            next = result?.OdataNextLink;
            ValidateNextLink(next);
            if (next is not null && !seenPages.Add(next)) throw new InvalidOperationException("Outlook 返回了重复的邮件分页地址。");
        } while (next is not null && messages.Count < limit);
        return messages;
    }
    private static void ValidateNextLink(string? next)
    {
        if (next is not null && (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "graph.microsoft.com"))
            throw new InvalidOperationException("Outlook 返回了不受信任的邮件分页地址。");
    }

    public async Task<List<OutlookMailboxFolder>> GetFoldersAsync(MailAccount account, CancellationToken token = default)
    {
        var folders = new List<OutlookMailboxFolder>();
        var pending = new Queue<(string? Id, string Path)>(); pending.Enqueue((null, ""));
        while (pending.TryDequeue(out var parent))
        {
            string? next = null;
            var seenPages = new HashSet<string>();
            do
            {
                var page = await RequestAsync(account, client =>
                {
                    if (next is not null) return client.Me.MailFolders.WithUrl(next).GetAsync(cancellationToken: token);
                    if (parent.Id is null) return client.Me.MailFolders.GetAsync(c => { c.QueryParameters.Top = 100; c.QueryParameters.Select = ["id", "displayName", "childFolderCount"]; }, token);
                    return client.Me.MailFolders[parent.Id].ChildFolders.GetAsync(c => { c.QueryParameters.Top = 100; c.QueryParameters.Select = ["id", "displayName", "childFolderCount"]; }, token);
                }, token);
                foreach (var folder in page?.Value ?? [])
                {
                    if (string.IsNullOrWhiteSpace(folder.Id)) continue;
                    var path = string.IsNullOrEmpty(parent.Path) ? folder.DisplayName ?? folder.Id : parent.Path + "/" + folder.DisplayName;
                    folders.Add(new(folder.Id, path, MailboxFolderClassifier.Classify(path)));
                    if (folder.ChildFolderCount > 0) pending.Enqueue((folder.Id, path));
                }
                next = page?.OdataNextLink;
                ValidateNextLink(next);
                if (next is not null && !seenPages.Add(next)) throw new InvalidOperationException("Outlook 返回了重复的文件夹分页地址。");
            } while (next is not null);
        }
        return folders;
    }

    private sealed class MailAuthentication(IMsaTokenManager manager, MailAccount account) : IAuthenticationProvider
    {
        public async Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            var uri = request.URI;
            if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com") throw new InvalidOperationException("Outlook 授权只能发送给微软邮件接口。");
            var access = await manager.GetGraphReadAccessTokenAsync(account, cancellationToken: cancellationToken);
            request.Headers.Add("Authorization", "Bearer " + access.AccessToken);
        }
    }
}
