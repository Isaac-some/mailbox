using MailArchiver.Data;
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
    private readonly ILogger<LocalMaintenanceController> _logger;

    public LocalMaintenanceController(
        MailArchiverDbContext context,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<LocalMaintenanceController> logger)
    {
        _context = context;
        _lifetime = lifetime;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearMailData()
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();
        await _context.EmailAttachments.ExecuteDeleteAsync();
        await _context.AttachmentContents.ExecuteDeleteAsync();
        await _context.ArchivedEmails.ExecuteDeleteAsync();
        await _context.SyncCheckpoints.ExecuteDeleteAsync();
        await _context.BandwidthUsages.ExecuteDeleteAsync();
        await _context.AccountStorageCaches.ExecuteDeleteAsync();
        await _context.AccountStorageBackfillStates.ExecuteDeleteAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("Local archive mail and attachment data cleared; accounts retained.");
        TempData["SuccessMessage"] = "邮件和附件已清空，邮箱账号已保留。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult FactoryReset(string confirmation)
    {
        if (!IsLocalApp())
        {
            return NotFound();
        }

        if (!string.Equals(confirmation?.Trim(), "RESET", StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = "请输入 RESET 后再执行恢复出厂设置。";
            return RedirectToAction(nameof(Index));
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
