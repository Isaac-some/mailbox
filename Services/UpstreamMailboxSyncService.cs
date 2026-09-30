using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using MailArchiver.Models;
using MailArchiver.Models.Api;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public sealed record UpstreamMailboxSyncResult(
    bool Enabled,
    int Created,
    int Updated,
    int Rejected,
    string? Error = null,
    string? Summary = null)
{
    public bool Succeeded => string.IsNullOrEmpty(Error);
}

public interface IUpstreamMailboxSyncService
{
    Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default);
    Task<UpstreamMailboxSyncResult> PullEmailAsync(int userId, string email, CancellationToken cancellationToken = default);
}

/// <summary>Pulls the external credential envelope before a user-triggered sync.</summary>
public sealed class UpstreamMailboxSyncService : IUpstreamMailboxSyncService
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly ConcurrentDictionary<(int UserId, string Email), byte> PriorityEmails = new();
    private static readonly ConcurrentDictionary<(int UserId, string Email), TaskCompletionSource<UpstreamMailboxSyncResult>> PriorityPulls = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MailCredentialIntakeService _intake;
    private readonly IUpstreamMailboxSyncCursorStore _cursorStore;
    private readonly UpstreamMailboxSyncOptions _options;
    private readonly ILogger<UpstreamMailboxSyncService> _logger;

    public UpstreamMailboxSyncService(
        IHttpClientFactory httpClientFactory,
        MailCredentialIntakeService intake,
        IUpstreamMailboxSyncCursorStore cursorStore,
        IOptions<UpstreamMailboxSyncOptions> options,
        ILogger<UpstreamMailboxSyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _intake = intake;
        _cursorStore = cursorStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (!await SyncGate.WaitAsync(0, cancellationToken))
            return Failed("正在同步中，请稍后再试。");
        try
        {
            PriorityEmails.Clear();
            return await PullCoreAsync(userId, cancellationToken);
        }
        finally
        {
            PriorityEmails.Clear();
            SyncGate.Release();
        }
    }

    public async Task<UpstreamMailboxSyncResult> PullEmailAsync(int userId, string email, CancellationToken cancellationToken = default)
    {
        var key = (userId, email.Trim().ToLowerInvariant());
        var completion = new TaskCompletionSource<UpstreamMailboxSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PriorityPulls.TryAdd(key, completion))
        {
            if (PriorityPulls.TryGetValue(key, out var existing))
                return await existing.Task.WaitAsync(cancellationToken);
            return await PullEmailAsync(userId, email, cancellationToken);
        }

        try
        {
            var result = await PullEmailCoreAsync(userId, email, cancellationToken);
            if (result.Succeeded && result.Enabled)
                PriorityEmails.TryAdd(key, 0);
            completion.TrySetResult(result);
            return result;
        }
        catch
        {
            completion.TrySetResult(Failed("该邮箱授权查询已中断。"));
            throw;
        }
        finally
        {
            PriorityPulls.TryRemove(key, out _);
        }
    }

    private async Task<UpstreamMailboxSyncResult> PullEmailCoreAsync(int userId, string email, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return new UpstreamMailboxSyncResult(false, 0, 0, 0);
        if (!TryCreateEndpoint(_options.Endpoint, out var endpoint))
            return Failed("线上账号接口地址未配置，必须使用 HTTPS。");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            return Failed("交付包缺少平台租户 Token，请联系交付方重新打包。");
        email = email.Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address)
            || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            return Failed("请输入完整的邮箱地址。");

        var token = _options.BearerToken.Trim();
        var metadata = CreateMetadata(endpoint, token);
        try
        {
            var client = _httpClientFactory.CreateClient("UpstreamMailboxSync");
            using var response = await SendPageAsync(client, BuildRequestUri(endpoint, 1, 1, null, email), metadata, token, cancellationToken);
            if (response is null)
                return Failed("线上账号接口没有返回响应。");
            if (!response.IsSuccessStatusCode)
                return Failed(MapHttpError(response.StatusCode, await TryReadErrorAsync(response, cancellationToken)));

            var payload = await response.Content.ReadFromJsonAsync<ExternalMailboxCredentialsResponse>(cancellationToken: cancellationToken);
            var data = payload?.Data;
            if (data is null || data.Total < 0 || data.Items is null)
                return Failed("线上账号接口返回格式不正确。");
            if (data.Total == 0)
                return Failed("平台未找到该邮箱的授权信息。");
            if (data.Total != 1 || data.Items.Count != 1 || data.Items[0] is not { } item
                || !string.Equals(item.Email, email, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Credential))
                return Failed("平台返回的邮箱授权信息不完整或与请求不匹配。");

            var isLocalApp = string.Equals(Environment.GetEnvironmentVariable("MAIL_ASSISTANT_LOCAL_APP"), "1", StringComparison.Ordinal);
            var result = await _intake.UpsertAsync(userId,
                new MailCredentialIntake(item.Email, item.Credential, item.Domain, item.ClientId),
                enabled: true, cancellationToken, verifyCredential: false,
                allowCrossUserCredentialUpdate: isLocalApp);
            return new UpstreamMailboxSyncResult(true, result.Created ? 1 : 0, result.Created ? 0 : 1, 0,
                Summary: "已优先更新该邮箱授权。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("该邮箱授权查询超时，请重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "Failed to pull priority mailbox credentials for {Email}", email);
            return Failed("该邮箱授权信息暂时无法更新，请稍后重试。");
        }
    }

    private async Task<UpstreamMailboxSyncResult> PullCoreAsync(int userId, CancellationToken cancellationToken)
    {
        var isLocalApp = string.Equals(Environment.GetEnvironmentVariable("MAIL_ASSISTANT_LOCAL_APP"), "1", StringComparison.Ordinal);
        if (!_options.Enabled)
            return new UpstreamMailboxSyncResult(false, 0, 0, 0);
        if (!TryCreateEndpoint(_options.Endpoint, out var endpoint))
            return Failed("线上账号接口地址未配置，必须使用 HTTPS。");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            return Failed("交付包缺少平台租户 Token，请联系交付方重新打包。");

        var bearerToken = _options.BearerToken.Trim();
        var metadata = CreateMetadata(endpoint, bearerToken);

        try
        {
            var updatedSince = await _cursorStore.ReadAsync(cancellationToken);
            var pageSize = Math.Clamp(_options.PageSize, 1, 1000);
            var maxItems = _options.MaxItems <= 0 ? int.MaxValue : _options.MaxItems;
            var client = _httpClientFactory.CreateClient("UpstreamMailboxSync");
            var firstPageCursor = string.Empty;
            var page = 1;
            var created = 0;
            var updated = 0;
            var rejected = 0;
            var processed = 0;
            var total = 0;

            while (true)
            {
                var requestUri = BuildRequestUri(endpoint, page, pageSize, updatedSince);
                using var response = await SendPageAsync(
                    client,
                    requestUri,
                    metadata,
                    bearerToken,
                    cancellationToken);

                if (response is null)
                    return Failed("线上账号接口没有返回响应。");
                if (!response.IsSuccessStatusCode)
                {
                    var error = await TryReadErrorAsync(response, cancellationToken);
                    return Failed(MapHttpError(response.StatusCode, error));
                }

                var payload = await response.Content.ReadFromJsonAsync<ExternalMailboxCredentialsResponse>(cancellationToken: cancellationToken);
                var data = payload?.Data;
                if (data is null || data.Total < 0)
                    return Failed("线上账号接口返回缺少有效的 data.total。");
                if (data.Page > 0 && data.Page != page)
                    return Failed($"线上账号接口返回页码错误：请求第 {page} 页，实际第 {data.Page} 页。");
                if (page == 1)
                {
                    firstPageCursor = ExtractCursor(data);
                    if (string.IsNullOrWhiteSpace(firstPageCursor))
                        return Failed("线上账号接口返回缺少有效的 data.serverTimestamp。");
                }

                total = data.Total;
                if (total > maxItems)
                    return Failed($"线上账号接口数据量 {total} 条，超过本地安全上限 {maxItems} 条。");
                var items = data.Items ?? [];
                if (processed + items.Count > maxItems)
                    return Failed($"线上账号接口数据量超过本地安全上限 {maxItems} 条。");

                foreach (var item in items)
                {
                    processed++;
                    if (item is null || string.IsNullOrWhiteSpace(item.Email) || string.IsNullOrWhiteSpace(item.Credential))
                    {
                        rejected++;
                        continue;
                    }
                    var priorityKey = (userId, item.Email.Trim().ToLowerInvariant());
                    if (PriorityPulls.TryGetValue(priorityKey, out var priorityPull))
                        await priorityPull.Task.WaitAsync(cancellationToken);
                    if (PriorityEmails.ContainsKey(priorityKey))
                        continue;

                    try
                    {
                        var result = await _intake.UpsertAsync(
                            userId,
                            new MailCredentialIntake(item.Email, item.Credential, item.Domain, item.ClientId),
                            enabled: true,
                            cancellationToken,
                            verifyCredential: false,
                            allowCrossUserCredentialUpdate: isLocalApp);
                        if (result.Created) created++; else updated++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                    {
                        rejected++;
                        _logger.LogWarning(ex, "Rejected upstream mailbox row for {Email}", item.Email);
                    }
                }

                var hasMore = data.HasMore ?? (data.PageSize > 0 && items.Count >= data.PageSize);
                if (!hasMore)
                {
                    if (processed != total)
                        return Failed($"线上账号接口数据不完整：声明 {total} 条，实际收到 {processed} 条。");
                    break;
                }
                if (items.Count == 0)
                    return Failed("线上账号接口声明还有下一页，但当前页没有数据。");
                page++;
            }

            try
            {
                // The cursor is written only after every page has succeeded.
                await _cursorStore.WriteAsync(firstPageCursor, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogError(ex, "Failed to persist upstream mailbox sync cursor");
                return Failed("同步游标无法保存，本次未完成；请检查应用数据目录权限后重试。");
            }

            var summary = rejected > 0
                ? $"平台账号已同步：新增 {created} 个，更新 {updated} 个，跳过 {rejected} 个。"
                : $"平台账号已同步：新增 {created} 个，更新 {updated} 个。";
            return new UpstreamMailboxSyncResult(true, created, updated, rejected, Summary: summary);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("线上账号接口请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Failed to pull mailbox credentials from upstream platform");
            return Failed("线上账号接口不可用或返回格式不正确。");
        }
    }

    private static UpstreamMailboxSyncResult Failed(string error) => new(true, 0, 0, 0, error);

    private static UpstreamMailboxConnection CreateMetadata(Uri endpoint, string token) => new(
        endpoint.AbsoluteUri, token, "server-config", Environment.MachineName,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        typeof(UpstreamMailboxSyncService).Assembly.GetName().Version?.ToString(3) ?? "unknown");

    private static void AddClientMetadataHeaders(HttpRequestMessage request, UpstreamMailboxConnection connection)
    {
        request.Headers.TryAddWithoutValidation("X-MailAssistant-Installation-Id", SanitizeHeader(connection.InstallationId));
        request.Headers.TryAddWithoutValidation("X-MailAssistant-Device-Name", SanitizeHeader(connection.DeviceName));
        request.Headers.TryAddWithoutValidation("X-MailAssistant-OS", SanitizeHeader(connection.OperatingSystem));
        request.Headers.TryAddWithoutValidation("X-MailAssistant-App-Version", SanitizeHeader(connection.AppVersion));
    }

    private static string SanitizeHeader(string value)
        => new(value.Where(character => !char.IsControl(character)).Take(200).ToArray());

    private async Task<HttpResponseMessage?> SendPageAsync(
        HttpClient client,
        Uri requestUri,
        UpstreamMailboxConnection metadata,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var serverRetries = 0;
        var maxServerRetries = Math.Clamp(_options.ServerErrorRetries, 1, 3);
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120)));
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            AddClientMetadataHeaders(request, metadata);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.RequestTimeout);
            }

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                && serverRetries < maxServerRetries)
            {
                response.Dispose();
                serverRetries++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.ServerErrorRetryDelaySeconds, 1, 60)), cancellationToken);
                continue;
            }

            return response;
        }
    }

    private static Uri BuildRequestUri(Uri endpoint, int page, int pageSize, string? updatedSince, string? email = null)
    {
        var builder = new UriBuilder(endpoint);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        query["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(updatedSince))
            query.Remove("updatedSince");
        else
            query["updatedSince"] = NormalizeUpdatedSince(updatedSince);
        if (string.IsNullOrWhiteSpace(email))
            query.Remove("email");
        else
            query["email"] = email;
        builder.Query = query.ToString() ?? string.Empty;
        return builder.Uri;
    }

    private static bool TryCreateEndpoint(string? value, out Uri endpoint)
    {
        endpoint = default!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (uri.AbsolutePath is "" or "/")
        {
            var builder = new UriBuilder(uri) { Path = "/api/external/account-credentials" };
            uri = builder.Uri;
        }
        endpoint = uri;
        return true;
    }

    private static string NormalizeUpdatedSince(string value)
    {
        var trimmed = value.Trim();
        if (long.TryParse(trimmed, out _))
            return trimmed;
        return DateTimeOffset.TryParse(trimmed, out var parsed)
            ? parsed.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static string ExtractCursor(ExternalMailboxCredentialsData data)
    {
        if (data.ServerTimestamp is JsonElement timestamp)
        {
            if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var integer))
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (timestamp.ValueKind == JsonValueKind.String)
            {
                var value = timestamp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        // Compatibility with pre-2026-09-28 servers. New responses must use
        // serverTimestamp, but a legacy serverTime remains a safe cursor.
        return data.ServerTime?.Trim() ?? string.Empty;
    }

    private static string MapHttpError(System.Net.HttpStatusCode statusCode, string? error)
    {
        var status = (int)statusCode;
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => error ?? "线上账号接口参数错误，请联系管理员。",
            System.Net.HttpStatusCode.Unauthorized => "平台租户 token 无效或已停用（HTTP 401），请联系管理员。",
            System.Net.HttpStatusCode.Forbidden => "平台租户 Token 没有读取邮箱凭据的权限（HTTP 403），请联系管理员。",
            System.Net.HttpStatusCode.InternalServerError => error ?? "线上账号接口暂时不可用（HTTP 500），请稍后重试。",
            _ => error ?? $"线上账号接口返回 HTTP {status}。"
        };
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return null;
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                ? error.GetString()?.Trim()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException)
        {
            return null;
        }
    }
}
