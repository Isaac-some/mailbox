using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Controllers;

public sealed class DesktopUpdatesController(DesktopUpdateService updates, MailArchiverDbContext context,
    ICsvImportService imports) : Controller
{
    [HttpGet]
    public IActionResult Index() => updates.Available ? View() : NotFound();
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status() => updates.Available ? Json(updates.State) : NotFound();
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Check(CancellationToken token) => updates.Available
        ? Json(await updates.CheckAsync(token)) : NotFound();
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Download(CancellationToken token) => updates.Available
        ? Json(await updates.StartDownloadAsync(token)) : NotFound();
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Install(CancellationToken token) => updates.Available
        ? Json(await updates.InstallAsync(async () => imports.HasActiveJobs()
            || await context.OutboundMailTaskItems.AnyAsync(i => i.Status == OutboundMailTaskItemStatus.Sending, token), token))
        : NotFound();
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Health() => updates.Available ? Json(new
    {
        version = updates.CurrentVersion,
        nonce = Environment.GetEnvironmentVariable("MAIL_ASSISTANT_UPDATE_NONCE"),
        processId = Environment.ProcessId
    }) : NotFound();
}
