using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    bool RequiresLogin = false)
{
    public bool Succeeded => string.IsNullOrEmpty(Error);
}

public interface IUpstreamMailboxSyncService
{
    Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default);
}

/// <summary>Pulls the external credential envelope before a user-triggered sync.</summary>
public sealed class UpstreamMailboxSyncService : IUpstreamMailboxSyncService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MailCredentialIntakeService _intake;
    private readonly IUpstreamMailboxConnectionStore _connectionStore;
    private readonly IUpstreamMailboxSyncCursorStore _cursorStore;
    private readonly UpstreamMailboxSyncOptions _options;
    private readonly IPlatformSessionStore _platformSessionStore;
    private readonly ILogger<UpstreamMailboxSyncService> _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public UpstreamMailboxSyncService(
        IHttpClientFactory httpClientFactory,
        MailCredentialIntakeService intake,
        IUpstreamMailboxConnectionStore connectionStore,
        IUpstreamMailboxSyncCursorStore cursorStore,
        IOptions<UpstreamMailboxSyncOptions> options,
        IPlatformSessionStore platformSessionStore,
        ILogger<UpstreamMailboxSyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _intake = intake;
        _connectionStore = connectionStore;
        _cursorStore = cursorStore;
        _options = options.Value;
        _platformSessionStore = platformSessionStore;
        _logger = logger;
    }

    // Kept for focused unit tests and integrations that construct the service directly.
    public UpstreamMailboxSyncService(
        IHttpClientFactory httpClientFactory,
        MailCredentialIntakeService intake,
        IUpstreamMailboxConnectionStore connectionStore,
        IUpstreamMailboxSyncCursorStore cursorStore,
        IOptions<UpstreamMailboxSyncOptions> options,
        ILogger<UpstreamMailboxSyncService> logger)
        : this(httpClientFactory, intake, connectionStore, cursorStore, options, new PlatformSessionStore(), logger)
    {
    }

    public async Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default)
    {
        UpstreamMailboxConnection? connection;
        try
        {
            connection = await _connectionStore.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogError(ex, "Failed to read upstream mailbox connection settings");
            return Failed("本机平台连接配置无法读取，请在本机数据页面重新配置。");
        }

        var platformSession = _platformSessionStore.Current;
        var isLocalApp = string.Equals(Environment.GetEnvironmentVariable("KOUZI_LOCAL_APP"), "1", StringComparison.Ordinal);
        // Packaged builds use the platform login session exclusively. Ignore
        // any legacy encrypted Token file left by an older release.
        if (isLocalApp)
            connection = null;
        if (connection is null && !_options.Enabled && platformSession is null)
            return new UpstreamMailboxSyncResult(false, 0, 0, 0);
        connection ??= new UpstreamMailboxConnection(
            _options.Endpoint,
            _options.BearerToken,
            "server-config",
            Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            typeof(UpstreamMailboxSyncService).Assembly.GetName().Version?.ToString(3) ?? "unknown");

        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
            return Failed("线上账号接口必须使用 HTTPS，且地址格式正确。");
        var bearerToken = platformSession?.BearerToken ?? connection.BearerToken;
        var cookieHeader = platformSession?.CookieHeader;
        if (_options.RequireBearerToken && string.IsNullOrWhiteSpace(bearerToken) && string.IsNullOrWhiteSpace(cookieHeader))
        {
            return isLocalApp
                ? Failed("平台登录会话不存在，请重新登录。", requiresLogin: true)
                : Failed("线上账号接口密钥未配置，已拒绝同步。");
        }

        try
        {
            var updatedSince = await _cursorStore.ReadAsync(cancellationToken);
            var requestUri = BuildRequestUri(endpoint, updatedSince);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120)));
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            AddClientMetadataHeaders(request, connection);

            var client = _httpClientFactory.CreateClient("UpstreamMailboxSync");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryReadErrorAsync(response, timeout.Token);
                if (platformSession is not null && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    _platformSessionStore.Clear();
                    return Failed(error ?? "平台账号已失效，请重新登录。", requiresLogin: true);
                }
                return Failed(error ?? $"线上账号接口返回 HTTP {(int)response.StatusCode}。");
            }

            var payload = await response.Content.ReadFromJsonAsync<ExternalMailboxCredentialsResponse>(cancellationToken: timeout.Token);
            var data = payload?.Data;
            if (data is null || !DateTimeOffset.TryParse(data.ServerTime, out _))
                return Failed("线上账号接口返回缺少有效的 data.serverTime。");

            var items = data.Items ?? [];
            if (data.Total < 0 || data.Total != items.Count)
                return Failed($"线上账号接口数据不完整：total={data.Total}，实际 {items.Count} 条。");
            if (items.Count > Math.Max(1, _options.MaxItems))
                return Failed($"线上账号接口一次返回 {items.Count} 条，超过上限 {_options.MaxItems} 条。");

            var created = 0;
            var updated = 0;
            var rejected = 0;
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Email) || string.IsNullOrWhiteSpace(item.Credential))
                {
                    rejected++;
                    continue;
                }

                try
                {
                    var result = await _intake.UpsertAsync(
                        userId,
                        new MailCredentialIntake(item.Email, item.Credential, item.Domain, item.ClientId),
                        enabled: true,
                        cancellationToken);
                    if (result.Created) created++; else updated++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    rejected++;
                    _logger.LogWarning(ex, "Rejected upstream mailbox row for {Email}", item.Email);
                }
            }

            if (rejected > 0)
                return new UpstreamMailboxSyncResult(true, created, updated, rejected,
                    $"线上账号有 {rejected} 条未通过导入校验，本次邮件同步已停止；请修正后重试。");

            try
            {
                await _cursorStore.WriteAsync(data.ServerTime, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to persist upstream mailbox sync cursor");
                return Failed("同步游标无法保存，本次未继续邮件同步；请检查应用数据目录权限后重试。");
            }
            return new UpstreamMailboxSyncResult(true, created, updated, 0);
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

    private static UpstreamMailboxSyncResult Failed(string error, bool requiresLogin = false) => new(true, 0, 0, 0, error, requiresLogin);

    private static void AddClientMetadataHeaders(HttpRequestMessage request, UpstreamMailboxConnection connection)
    {
        request.Headers.TryAddWithoutValidation("X-Kouzi-Installation-Id", SanitizeHeader(connection.InstallationId));
        request.Headers.TryAddWithoutValidation("X-Kouzi-Device-Name", SanitizeHeader(connection.DeviceName));
        request.Headers.TryAddWithoutValidation("X-Kouzi-OS", SanitizeHeader(connection.OperatingSystem));
        request.Headers.TryAddWithoutValidation("X-Kouzi-App-Version", SanitizeHeader(connection.AppVersion));
    }

    private static string SanitizeHeader(string value)
        => new(value.Where(character => !char.IsControl(character)).Take(200).ToArray());

    private static Uri BuildRequestUri(Uri endpoint, string? updatedSince)
    {
        if (string.IsNullOrWhiteSpace(updatedSince))
            return endpoint;
        var builder = new UriBuilder(endpoint);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["updatedSince"] = updatedSince;
        builder.Query = query.ToString() ?? string.Empty;
        return builder.Uri;
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ExternalMailboxCredentialsResponse>(cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(payload?.Error) ? null : payload.Error.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
