using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MailAssistant.Updates;

namespace MailArchiver.Services;

public sealed record DesktopUpdateState(string Phase, string CurrentVersion, string? LatestVersion = null,
    string? Notes = null, long Downloaded = 0, long Total = 0, string? Message = null);

public sealed class DesktopUpdateService(
    IHttpClientFactory clients, IConfiguration configuration, IHostApplicationLifetime lifetime,
    DesktopUpdateActivity activity, ILogger<DesktopUpdateService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DesktopUpdateState? _state;
    private UpdateManifest? _manifest;
    private UpdateArtifact? _artifact;
    private string? _session;
    private byte[]? _manifestBytes;
    private byte[]? _signature;

    public string CurrentVersion => configuration["ReleaseNotes:AppVersion"]
        ?? typeof(DesktopUpdateService).Assembly.GetName().Version!.ToString(3);
    public string Platform => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "osx-arm64" : OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "unsupported";
    public bool Available => Environment.GetEnvironmentVariable("MAIL_ASSISTANT_LOCAL_APP") == "1"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAIL_ASSISTANT_INSTALL_DIRECTORY"))
        && Platform != "unsupported";
    public DesktopUpdateState State => Volatile.Read(ref _state) ?? new("idle", CurrentVersion,
        Message: ReadLastResult());
    private string DataDirectory => Environment.GetEnvironmentVariable("MAIL_ASSISTANT_DATA_DIRECTORY")
        ?? throw new InvalidOperationException("应用数据目录缺失。");
    private string Channel => Environment.GetEnvironmentVariable("MAIL_ASSISTANT_UPDATE_TEST_TAG") ?? "stable";

    public async Task<DesktopUpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Available) throw new InvalidOperationException("请在桌面应用中检查更新。");
        if (!await _gate.WaitAsync(0, cancellationToken)) return State;
        try
        {
            if (State.Phase is "downloading" or "installing") return State;
            if (State.Phase == "ready") return State;
            Set(new("checking", CurrentVersion, Message: "正在检查更新…"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            // Fixed public release assets avoid the shared-IP anonymous GitHub API quota.
            // Test channels are selected only at native launch, never by a browser request.
            var releasePath = Channel == "stable" ? "latest/download" : "download/" + Uri.EscapeDataString(Channel);
            var releaseBase = $"https://github.com/{UpdateProtocol.Repository}/releases/{releasePath}/";
            _manifestBytes = await GetSmallAsync(new Uri(releaseBase + "desktop-update.json"), 100_000, timeout.Token);
            _signature = await GetSmallAsync(new Uri(releaseBase + "desktop-update.sig"), 1024, timeout.Token);
            var manifest = UpdateProtocol.VerifyManifest(_manifestBytes, _signature);
            if ((Channel == "stable" && manifest.Prerelease) || (Channel != "stable" && manifest.Tag != Channel))
                throw new InvalidOperationException("更新版本与签名信息不一致。");
            _manifest = manifest;
            _artifact = manifest.Artifacts.SingleOrDefault(a => a.Platform == Platform)
                ?? throw new InvalidOperationException("新版暂未提供当前系统的更新包。");
            var newer = UpdateProtocol.IsNewer(manifest.Version, CurrentVersion);
            Set(new(newer ? "available" : "current", CurrentVersion, manifest.Version,
                manifest.Notes,
                Total: _artifact.Size, Message: newer ? "发现新版本，可下载更新。" : "当前已是最新版本。"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Desktop update check failed");
            Set(new("error", CurrentVersion, Message: FriendlyError(exception)));
        }
        finally { _gate.Release(); }
        return State;
    }

    public async Task<DesktopUpdateState> StartDownloadAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return State;
        try
        {
            if (_manifest is null || _artifact is null || State.Phase is not ("available" or "error")) return State;
            _session = Path.Combine(DataDirectory, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_session);
            Set(State with { Phase = "downloading", Downloaded = 0, Total = _artifact.Size, Message = "正在下载更新…" });
            // Deliberately independent of the HTTP request: leaving the page does not cancel the download.
            _ = Task.Run(DownloadAsync);
            return State;
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadAsync()
    {
        var path = Path.Combine(_session!, "archive.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await GetAsync(new Uri(_artifact!.Url), timeout.Token);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += count;
                    if (total > _artifact.Size) throw new InvalidOperationException("更新文件大小不匹配。");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    Set(State with { Downloaded = total });
                }
            }
            await UpdateProtocol.VerifyArchiveAsync(path, _artifact!, timeout.Token);
            await File.WriteAllBytesAsync(Path.Combine(_session!, "desktop-update.json"), _manifestBytes!, timeout.Token);
            await File.WriteAllBytesAsync(Path.Combine(_session!, "desktop-update.sig"), _signature!, timeout.Token);
            Set(State with { Phase = "ready", Message = "下载完成，重启后安装更新。" });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Desktop update download failed");
            if (File.Exists(path)) File.Delete(path);
            Set(State with { Phase = "error", Message = FriendlyError(exception) });
        }
    }

    public async Task<DesktopUpdateState> InstallAsync(Func<Task<bool>> hasPendingWork, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return State;
        var acquired = false;
        try
        {
            if (State.Phase != "ready" || _session is null) return State;
            if (!activity.TryBeginInstall())
            {
                Set(State with { Message = "正在发送邮件，请等待发送完成后再更新。" });
                return State;
            }
            acquired = true;
            if (await hasPendingWork())
            {
                activity.CancelInstall(); acquired = false;
                Set(State with { Message = "后台任务正在处理，请等待完成后再更新。" });
                return State;
            }
            Set(State with { Phase = "preparing", Message = "正在校验并准备安装，请稍候…" });
            var installDirectory = Environment.GetEnvironmentVariable("MAIL_ASSISTANT_INSTALL_DIRECTORY")!;
            var nativePid = int.Parse(Environment.GetEnvironmentVariable("MAIL_ASSISTANT_NATIVE_PID")!);
            var port = int.Parse(Environment.GetEnvironmentVariable("MAIL_ASSISTANT_PORT") ?? "5180");
            foreach (var markerName in new[] { "installer-ready", "install-approved", "install-aborted" })
            {
                var marker = Path.Combine(_session, markerName);
                if (File.Exists(marker)) File.Delete(marker);
            }
            var plan = new UpdateInstallPlan(_session, installDirectory, DataDirectory, Platform,
                nativePid, Environment.ProcessId, CurrentVersion, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), port);
            UpdateProtocol.WriteJson(Path.Combine(_session, "plan.json"), plan);
            var fileName = OperatingSystem.IsWindows() ? "MailAssistant.Updater.exe" : "MailAssistant.Updater";
            var helper = Path.Combine(_session, fileName);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "updater", fileName), helper, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _session };
            start.ArgumentList.Add(Path.Combine(_session, "plan.json"));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新安装程序。");
            var ready = Path.Combine(_session, "installer-ready");
            for (var i = 0; i < 600; i++)
            {
                if (File.Exists(ready))
                {
                    UpdateProtocol.WriteJson(Path.Combine(DataDirectory, "update-request.json"), new { session = _session });
                    await File.WriteAllTextAsync(Path.Combine(_session, "install-approved"), plan.Nonce, cancellationToken);
                    Set(State with { Phase = "installing", Message = "正在重启并安装更新…" });
                    _ = Task.Run(async () => { await Task.Delay(1000); lifetime.StopApplication(); });
                    return State;
                }
                if (process.HasExited) throw new InvalidOperationException("更新准备失败，当前版本未改动。");
                await Task.Delay(200, cancellationToken);
            }
            process.Kill();
            throw new InvalidOperationException("更新准备超时，当前版本未改动。");
        }
        catch (Exception exception)
        {
            if (acquired) activity.CancelInstall();
            if (_session is not null)
            {
                File.WriteAllText(Path.Combine(_session, "install-aborted"), "1");
                var marker = Path.Combine(DataDirectory, "update-request.json");
                if (File.Exists(marker)) File.Delete(marker);
            }
            logger.LogWarning(exception, "Desktop update install preparation failed");
            Set(State with { Phase = "ready", Message = FriendlyError(exception) });
            return State;
        }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> GetSmallAsync(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidOperationException("更新信息过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        // The project's network-policy handler intentionally disables redirects. Follow only GitHub's release CDN.
        using var client = clients.CreateClient("DesktopUpdates");
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MailAssistant-DesktopUpdater/1.0");
        for (var i = 0; i < 6; i++)
        {
            if (uri.Scheme != "https" || !(uri.Host is "github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidOperationException("更新下载被重定向至未知来源。");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Update server HTTP {(int)status}", null, status);
            }
            return response;
        }
        throw new InvalidOperationException("更新下载重定向过多。");
    }

    private string? ReadLastResult()
    {
        try
        {
            var path = Path.Combine(DataDirectory, "update-result.json");
            if (!File.Exists(path)) return null;
            using var result = JsonDocument.Parse(File.ReadAllBytes(path));
            return result.RootElement.GetProperty("message").GetString();
        }
        catch { return null; }
    }
    private void Set(DesktopUpdateState state) => Volatile.Write(ref _state, state);
    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "无法连接更新服务，请检查网络或代理后重试。",
        OperationCanceledException => "更新请求超时或已取消，请重试。",
        InvalidOperationException => exception.Message,
        _ => "更新未完成，当前版本已保留，请重试。"
    };
}
