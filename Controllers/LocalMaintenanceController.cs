using MailArchiver.Data;
using MailArchiver.Services;
using MailArchiver.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace MailArchiver.Controllers;

/// <summary>
/// Destructive maintenance actions exposed only by the packaged local application.
/// The native shell performs the final filesystem and Keychain removal after shutdown.
/// </summary>
public sealed class LocalMaintenanceController : Controller
{
    private readonly MailArchiverDbContext _context;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _configuration;
    private readonly IUpstreamMailboxConnectionStore _connectionStore;
    private readonly IUpstreamMailboxSyncCursorStore _cursorStore;
    private readonly IUpstreamMailboxSyncService _upstreamSync;
    private readonly IPlatformSessionStore _platformSessionStore;
    private readonly ICsvImportService _csvImportService;
    private readonly ILogger<LocalMaintenanceController> _logger;

    public LocalMaintenanceController(
        MailArchiverDbContext context,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        IUpstreamMailboxConnectionStore connectionStore,
        IUpstreamMailboxSyncCursorStore cursorStore,
        IUpstreamMailboxSyncService upstreamSync,
        IPlatformSessionStore platformSessionStore,
        ICsvImportService csvImportService,
        ILogger<LocalMaintenanceController> logger)
    {
        _context = context;
        _lifetime = lifetime;
        _configuration = configuration;
        _connectionStore = connectionStore;
        _cursorStore = cursorStore;
        _upstreamSync = upstreamSync;
        _platformSessionStore = platformSessionStore;
        _csvImportService = csvImportService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        var connection = await _connectionStore.GetStatusAsync(cancellationToken);
        var session = _platformSessionStore.Current;
        return View(new LocalMaintenanceViewModel
        {
            PlatformConfigured = session is not null,
            PlatformUsername = session?.Username ?? string.Empty,
            PlatformIsAdmin = session?.IsAdmin ?? false,
            PlatformEndpoint = connection.Endpoint ?? string.Empty,
            InstallationId = connection.InstallationId,
            DeviceName = connection.DeviceName,
            OperatingSystem = connection.OperatingSystem,
            AppVersion = connection.AppVersion
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PullPlatformMailboxes(CancellationToken cancellationToken)
    {
        if (!IsLocalApp())
            return NotFound();

        var authentication = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
        var userId = authentication.GetCurrentUserId(HttpContext);
        if (!userId.HasValue)
            return Unauthorized();

        var result = await _upstreamSync.PullAsync(userId.Value, cancellationToken);
        if (!result.Enabled)
        {
            TempData["ErrorMessage"] = "请先配置平台连接。";
            return RedirectToAction(nameof(Index));
        }
        if (!result.Succeeded)
        {
            if (result.RequiresLogin)
            {
                return RedirectToAction("Login", "Auth", new { returnUrl = Url.Action(nameof(Index)) });
            }
            TempData["ErrorMessage"] = result.Error;
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = $"平台邮箱已更新：新增 {result.Created} 个，更新 {result.Updated} 个。";
        return RedirectToAction("Index", "MailAccounts");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePlatformConnection(CancellationToken cancellationToken)
    {
        if (!IsLocalApp())
            return NotFound();

        await _connectionStore.RemoveAsync(cancellationToken);
        _platformSessionStore.Clear();
        await _cursorStore.ResetAsync(cancellationToken);
        TempData["SuccessMessage"] = "平台连接已移除。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearMailData()
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        if (_csvImportService.HasActiveJobs())
        {
            TempData["ErrorMessage"] = "邮箱账号正在后台导入，请等待导入完成后再清空邮件和附件。";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _context.EmailAttachments.ExecuteDeleteAsync();
            await _context.AttachmentContents.ExecuteDeleteAsync();
            await _context.ArchivedEmails.ExecuteDeleteAsync();
            await _context.SyncCheckpoints.ExecuteDeleteAsync();
            await _context.BandwidthUsages.ExecuteDeleteAsync();
            await _context.AccountStorageCaches.ExecuteDeleteAsync();
            await _context.AccountStorageBackfillStates.ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear local mail data; accounts were not targeted");
            TempData["ErrorMessage"] = "邮件数据清理失败，邮箱账号和登录信息没有被删除。请重启应用后重试。";
            return RedirectToAction(nameof(Index));
        }

        _logger.LogInformation("Local archive mail and attachment data cleared; accounts retained.");
        TempData["SuccessMessage"] = "邮件和附件已清空，邮箱账号已保留。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult FactoryReset()
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        var markerPath = Environment.GetEnvironmentVariable("KOUZI_FACTORY_RESET_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            _logger.LogError("Factory reset was requested without a native reset marker path.");
            TempData["ErrorMessage"] = "无法执行恢复出厂设置，请退出后重新打开应用。";
            return RedirectToAction(nameof(Index));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        System.IO.File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        _logger.LogWarning("Local factory reset requested. Native shell will erase app data after shutdown.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            _lifetime.StopApplication();
        });

        return View("Resetting");
    }

    private bool IsLocalApp() => _configuration.GetValue<bool>("LocalApp:Enabled") ||
                                 string.Equals(Environment.GetEnvironmentVariable("KOUZI_LOCAL_APP"), "1", StringComparison.Ordinal);
}
