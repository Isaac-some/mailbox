using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public sealed record PlatformLoginResult(bool Succeeded, string? Error = null)
{
    public bool RequiresLogin => !Succeeded;
}

public interface IPlatformAuthenticationClient
{
    Task<PlatformLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
}

public sealed class PlatformAuthenticationClient : IPlatformAuthenticationClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlatformAuthenticationOptions _options;
    private readonly IPlatformSessionStore _sessionStore;
    private readonly ILogger<PlatformAuthenticationClient> _logger;

    public PlatformAuthenticationClient(
        IHttpClientFactory httpClientFactory,
        IOptions<PlatformAuthenticationOptions> options,
        IPlatformSessionStore sessionStore,
        ILogger<PlatformAuthenticationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task<PlatformLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new PlatformLoginResult(false, "请输入平台账号和密码。");

        if (!TryBuildUri(_options.BaseUrl, _options.LoginPath, out var loginUri))
            return new PlatformLoginResult(false, "平台登录地址配置无效，必须使用 HTTPS。");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120)));
            using var request = new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
                Content = JsonContent.Create(new { username = username.Trim(), password })
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("X-Kouzi-Device-Name", SanitizeHeader(Environment.MachineName));
            request.Headers.TryAddWithoutValidation("X-Kouzi-OS", SanitizeHeader(RuntimeInformation.OSDescription));
            request.Headers.TryAddWithoutValidation("X-Kouzi-App-Version", SanitizeHeader(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown"));

            var client = _httpClientFactory.CreateClient("PlatformAuthentication");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return FailAndClear("平台账号或密码错误。", response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return FailAndClear("平台账号已失效，请联系管理员。", response.StatusCode);
            if (!response.IsSuccessStatusCode)
                return FailAndClear(ReadError(body) ?? $"平台登录失败（HTTP {(int)response.StatusCode}）。", response.StatusCode);

            using var document = ParseJson(body);
            var bearerToken = ReadString(document, "accessToken")
                ?? ReadString(document, "token")
                ?? ReadString(document, "access_token")
                ?? ReadNestedString(document, "data", "accessToken")
                ?? ReadNestedString(document, "data", "token")
                ?? ReadNestedString(document, "data", "access_token");
            var isAdmin = ReadBoolean(document, "isAdmin")
                ?? ReadNestedBoolean(document, "data", "isAdmin")
                ?? string.Equals(ReadString(document, "role") ?? ReadNestedString(document, "data", "role"), "admin", StringComparison.OrdinalIgnoreCase);
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values.Select(value => value.Split(';', 2)[0]).Where(value => !string.IsNullOrWhiteSpace(value)))
                : null;

            if (string.IsNullOrWhiteSpace(bearerToken) && string.IsNullOrWhiteSpace(cookies))
                return FailAndClear("平台登录成功但没有返回会话凭据，无法安全同步邮箱。", response.StatusCode);

            _sessionStore.Set(new PlatformSession(username.Trim(), isAdmin, bearerToken, cookies, DateTimeOffset.UtcNow));
            _logger.LogInformation("Platform login succeeded for {Username}; role={Role}", username.Trim(), isAdmin ? "admin" : "user");
            return new PlatformLoginResult(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformLoginResult(false, "平台登录请求超时。");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Platform login request failed");
            return new PlatformLoginResult(false, "无法连接平台，请检查网络后重试。");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Platform login returned malformed JSON");
            return FailAndClear("平台登录返回格式无效，无法建立安全会话。", null);
        }
    }

    private PlatformLoginResult FailAndClear(string message, HttpStatusCode? statusCode)
    {
        _sessionStore.Clear();
        if (statusCode is not null)
            _logger.LogWarning("Platform login failed with HTTP {StatusCode}: {Message}", (int)statusCode.Value, message);
        return new PlatformLoginResult(false, message);
    }

    private static bool TryBuildUri(string baseUrl, string path, out Uri uri)
    {
        uri = default!;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!Uri.TryCreate(baseUri, path?.TrimStart('/') ?? string.Empty, out uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return true;
    }

    private static string SanitizeHeader(string value)
        => new(value.Where(character => !char.IsControl(character)).Take(200).ToArray());

    private static JsonDocument? ParseJson(string body)
        => string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);

    private static string? ReadError(string body)
    {
        try
        {
            using var document = ParseJson(body);
            return ReadString(document, "error") ?? ReadNestedString(document, "data", "error");
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadString(JsonDocument? document, string property)
        => document?.RootElement.TryGetProperty(property, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ReadNestedString(JsonDocument? document, string parent, string property)
        => document?.RootElement.TryGetProperty(parent, out var value) == true && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.String
            ? nested.GetString() : null;

    private static bool? ReadBoolean(JsonDocument? document, string property)
        => document?.RootElement.TryGetProperty(property, out var value) == true && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static bool? ReadNestedBoolean(JsonDocument? document, string parent, string property)
        => document?.RootElement.TryGetProperty(parent, out var value) == true && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var nested) && nested.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? nested.GetBoolean() : null;
}
