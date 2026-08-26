using System.Text;
using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Controllers;

[SelfManagerRequired]
public sealed class OutboundMailTasksController : Controller
{
    private const int MaxRows = 5000;
    private const long MaxFileBytes = 5_000_000;

    private readonly MailArchiverDbContext _context;
    private readonly IAuthenticationService _authentication;
    private readonly OutboundMailOptions _outboundOptions;
    private readonly TimeZoneInfo _displayTimeZone;

    public OutboundMailTasksController(
        MailArchiverDbContext context,
        IAuthenticationService authentication,
        IOptions<OutboundMailOptions> outboundOptions,
        IOptions<TimeZoneOptions> timeZoneOptions)
    {
        _context = context;
        _authentication = authentication;
        _outboundOptions = outboundOptions.Value;
        _displayTimeZone = ResolveTimeZone(timeZoneOptions.Value.DisplayTimeZoneId);
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!_outboundOptions.Enabled)
            return NotFound();

        var userId = RequireUserId();
        return View(await BuildIndexModelAsync(userId, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileBytes)]
    public async Task<IActionResult> Import(OutboundMailTaskIndexViewModel model, CancellationToken cancellationToken)
    {
        if (!_outboundOptions.Enabled)
            return NotFound();

        var userId = RequireUserId();
        var errors = new List<OutboundMailTaskCsvErrorViewModel>();
        if (model.CsvFile is null || model.CsvFile.Length == 0)
        {
            errors.Add(new(1, "请选择 CSV 文件。"));
            return View("Index", await BuildIndexModelAsync(userId, cancellationToken, errors));
        }
        if (model.CsvFile.Length > MaxFileBytes)
        {
            errors.Add(new(1, "CSV 文件不能超过 5 MB。"));
            return View("Index", await BuildIndexModelAsync(userId, cancellationToken, errors));
        }

        OutboundMailTaskCsvParseResult parsed;
        await using (var stream = model.CsvFile.OpenReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            parsed = OutboundMailTaskCsvParser.Parse(reader, DateTime.UtcNow, _displayTimeZone);
        }

        errors.AddRange(parsed.Errors.Select(error => new OutboundMailTaskCsvErrorViewModel(error.LineNumber, error.Reason)));
        if (parsed.Rows.Count == 0 && errors.Count == 0)
            errors.Add(new(1, "CSV 没有可发送的数据行。"));
        if (parsed.Rows.Count > MaxRows)
            errors.Add(new(1, $"每个任务最多 {MaxRows} 行。"));

        var accounts = await GetManageableAccountsAsync(userId, cancellationToken);
        var accountByEmail = accounts
            .Where(account => account.IsEnabled && OutboundMailService.CanAttemptSend(account))
            .GroupBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsed.Rows)
        {
            if (!accountByEmail.ContainsKey(row.Sender))
            {
                errors.Add(new(
                    row.LineNumber,
                    $"发件邮箱 {row.Sender} 未导入、已停用、无权使用或没有可用发件授权。"));
            }
        }

        if (errors.Count > 0)
            return View("Index", await BuildIndexModelAsync(userId, cancellationToken, errors));

        var task = new OutboundMailTask
        {
            CreatedByUserId = userId,
            Name = Path.GetFileNameWithoutExtension(model.CsvFile.FileName).Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Items = parsed.Rows.Select(row => new OutboundMailTaskItem
            {
                MailAccountId = accountByEmail[row.Sender].Id,
                CsvRowNumber = row.LineNumber,
                ScheduledAtUtc = row.ScheduledAtUtc,
                Recipient = row.Recipient,
                Subject = row.Subject,
                Body = row.Body,
                Status = OutboundMailTaskItemStatus.Pending
            }).ToList()
        };
        if (string.IsNullOrWhiteSpace(task.Name))
            task.Name = $"发件任务 {DateTime.Now:yyyy-MM-dd HH:mm}";

        _context.OutboundMailTasks.Add(task);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = $"发件任务已创建，共 {task.Items.Count} 封；系统会按 CSV 时间自动发送。";
        return RedirectToAction(nameof(Details), new { id = task.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        if (!_outboundOptions.Enabled)
            return NotFound();

        var userId = RequireUserId();
        var task = await _context.OutboundMailTasks
            .AsNoTracking()
            .Include(candidate => candidate.Items)
                .ThenInclude(item => item.MailAccount)
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.CreatedByUserId == userId, cancellationToken);
        if (task is null)
            return NotFound();

        return View(new OutboundMailTaskDetailsViewModel
        {
            Id = task.Id,
            Name = task.Name,
            CreatedAtUtc = task.CreatedAtUtc,
            TimeZoneId = _displayTimeZone.Id,
            Items = task.Items
                .OrderBy(item => item.ScheduledAtUtc)
                .ThenBy(item => item.Id)
                .Select(item => new OutboundMailTaskItemViewModel
                {
                    Id = item.Id,
                    CsvRowNumber = item.CsvRowNumber,
                    Sender = item.MailAccount.EmailAddress,
                    Recipient = item.Recipient,
                    Subject = item.Subject,
                    ScheduledAtUtc = item.ScheduledAtUtc,
                    Status = item.Status,
                    MessageId = item.MessageId,
                    ErrorMessage = item.ErrorMessage
                })
                .ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(int id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var item = await _context.OutboundMailTaskItems
            .Include(candidate => candidate.OutboundMailTask)
            .Include(candidate => candidate.MailAccount)
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.OutboundMailTask.CreatedByUserId == userId, cancellationToken);
        if (item is null)
            return NotFound();
        if (item.Status != OutboundMailTaskItemStatus.Failed)
        {
            TempData["ErrorMessage"] = "只有明确失败且未确认发出的邮件可以重试。";
            return RedirectToAction(nameof(Details), new { id = item.OutboundMailTaskId });
        }
        if (!item.MailAccount.IsEnabled || !OutboundMailService.CanAttemptSend(item.MailAccount))
        {
            TempData["ErrorMessage"] = "发件账号当前不可用，请先修复账号授权。";
            return RedirectToAction(nameof(Details), new { id = item.OutboundMailTaskId });
        }

        item.Status = OutboundMailTaskItemStatus.Pending;
        item.ScheduledAtUtc = DateTime.UtcNow;
        item.StartedAtUtc = null;
        item.CompletedAtUtc = null;
        item.ErrorMessage = null;
        item.MessageId = null;
        item.SentCopySaved = null;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "失败邮件已重新排队。";
        return RedirectToAction(nameof(Details), new { id = item.OutboundMailTaskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var task = await _context.OutboundMailTasks
            .Include(candidate => candidate.Items)
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.CreatedByUserId == userId, cancellationToken);
        if (task is null)
            return NotFound();

        foreach (var item in task.Items.Where(item => item.Status == OutboundMailTaskItemStatus.Pending))
        {
            item.Status = OutboundMailTaskItemStatus.Cancelled;
            item.CompletedAtUtc = DateTime.UtcNow;
            item.ErrorMessage = "任务已取消。";
        }
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "尚未开始的邮件已取消。";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        var content = "\uFEFF时间,发件邮箱,收件邮箱,主题,正文\r\n" +
                      "2026-08-24 15:30:00,sender1@outlook.com,target@gmx.com,测试邮件 01,这是第 1 封测试邮件。\r\n" +
                      "2026-08-24 15:31:00,sender2@outlook.com,target@gmx.com,测试邮件 02,\"正文可以包含逗号，也可以每行不同。\"\r\n";
        return File(Encoding.UTF8.GetBytes(content), "text/csv; charset=utf-8", "发件任务模板.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileBytes)]
    public async Task<IActionResult> PreviewSenderSelection(
        OutboundSenderMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!_outboundOptions.Enabled)
            return NotFound();

        var recognized = OutboundSenderListParser.ExtractFromText(request.SourceText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (request.ListFile is { Length: > 0 })
        {
            if (request.ListFile.Length > MaxFileBytes)
                return BadRequest(new { message = "名单文件不能超过 5 MB。" });

            try
            {
                await using var stream = request.ListFile.OpenReadStream();
                recognized.UnionWith(await OutboundSenderListParser.ExtractFromFileAsync(
                    stream,
                    request.ListFile.FileName,
                    cancellationToken));
            }
            catch (InvalidDataException exception)
            {
                return BadRequest(new { message = exception.Message });
            }
        }

        if (recognized.Count == 0)
            return BadRequest(new { message = "没有识别到完整邮箱地址。" });
        if (recognized.Count > MaxRows)
            return BadRequest(new { message = $"一次最多匹配 {MaxRows:N0} 个邮箱。" });

        var userId = RequireUserId();
        var accounts = await GetManageableAccountsAsync(userId, cancellationToken);
        var accountGroups = accounts
            .GroupBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var matchedAccounts = new List<MailAccount>();
        var unavailableEmails = new List<string>();
        var unmatchedEmails = new List<string>();
        foreach (var email in recognized.OrderBy(email => email, StringComparer.OrdinalIgnoreCase))
        {
            if (!accountGroups.TryGetValue(email, out var candidates))
            {
                unmatchedEmails.Add(email);
                continue;
            }

            var available = candidates.FirstOrDefault(account =>
                account.IsEnabled && OutboundMailService.CanAttemptSend(account));
            if (available is null)
            {
                unavailableEmails.Add(email);
                continue;
            }

            matchedAccounts.Add(available);
        }

        return Json(new
        {
            recognizedCount = recognized.Count,
            matchedCount = matchedAccounts.Count,
            unavailableCount = unavailableEmails.Count,
            unmatchedCount = unmatchedEmails.Count,
            matchedAccountIds = matchedAccounts.Select(account => account.Id),
            matchedEmails = matchedAccounts.Take(8).Select(account => account.EmailAddress),
            unavailableEmails = unavailableEmails.Take(8),
            unmatchedEmails = unmatchedEmails.Take(8)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadSenderCsv(
        OutboundSenderDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_outboundOptions.Enabled)
            return NotFound();

        var selectedIds = request.SelectedAccountIds.Distinct().ToList();
        if (selectedIds.Count == 0)
            return BadRequest("请至少选择一个可发件邮箱。");
        if (selectedIds.Count > MaxRows)
            return BadRequest($"一次最多导出 {MaxRows:N0} 个邮箱。");

        var userId = RequireUserId();
        var accounts = await ManageableAccounts(userId)
            .AsNoTracking()
            .Where(account => selectedIds.Contains(account.Id))
            .ToListAsync(cancellationToken);
        var availableAccounts = accounts
            .Where(account => account.IsEnabled && OutboundMailService.CanAttemptSend(account))
            .ToList();
        if (availableAccounts.Count != selectedIds.Count)
            return BadRequest("账号列表或发件状态已经变化，请刷新页面后重新选择。");

        var content = OutboundSenderListParser.BuildOutboundCsv(
            availableAccounts.Select(account => account.EmailAddress));
        return File(
            content,
            "text/csv; charset=utf-8",
            $"发件任务-已选邮箱-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    private async Task<OutboundMailTaskIndexViewModel> BuildIndexModelAsync(
        int userId,
        CancellationToken cancellationToken,
        List<OutboundMailTaskCsvErrorViewModel>? errors = null)
    {
        var accounts = await GetManageableAccountsAsync(userId, cancellationToken);
        var tasks = await _context.OutboundMailTasks
            .AsNoTracking()
            .Where(task => task.CreatedByUserId == userId)
            .Include(task => task.Items)
            .OrderByDescending(task => task.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        return new OutboundMailTaskIndexViewModel
        {
            TimeZoneId = _displayTimeZone.Id,
            ImportErrors = errors ?? [],
            SenderAccounts = accounts
                .GroupBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var selected = group.FirstOrDefault(account =>
                            account.IsEnabled && OutboundMailService.CanAttemptSend(account))
                        ?? group.OrderBy(account => account.Id).First();
                    return new OutboundSenderAccountViewModel
                    {
                        Id = selected.Id,
                        Name = selected.Name,
                        EmailAddress = selected.EmailAddress,
                        IsEnabled = selected.IsEnabled,
                        CanSend = selected.IsEnabled && OutboundMailService.CanAttemptSend(selected)
                    };
                })
                .OrderBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Tasks = tasks.Select(task => new OutboundMailTaskSummaryViewModel
            {
                Id = task.Id,
                Name = task.Name,
                CreatedAtUtc = task.CreatedAtUtc,
                Total = task.Items.Count,
                Pending = task.Items.Count(item => item.Status == OutboundMailTaskItemStatus.Pending),
                Sending = task.Items.Count(item => item.Status == OutboundMailTaskItemStatus.Sending),
                Sent = task.Items.Count(item => item.Status is OutboundMailTaskItemStatus.Sent or OutboundMailTaskItemStatus.SentWithoutCopy),
                Failed = task.Items.Count(item => item.Status == OutboundMailTaskItemStatus.Failed),
                Interrupted = task.Items.Count(item => item.Status == OutboundMailTaskItemStatus.Interrupted),
                Cancelled = task.Items.Count(item => item.Status == OutboundMailTaskItemStatus.Cancelled)
            }).ToList()
        };
    }

    private IQueryable<MailAccount> ManageableAccounts(int userId)
        => MailAccountManagementScope.Apply(
            _context.MailAccounts,
            userId,
            _authentication.IsCurrentUserAdmin(HttpContext));

    private Task<List<MailAccount>> GetManageableAccountsAsync(
        int userId,
        CancellationToken cancellationToken)
        => ManageableAccounts(userId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    private int RequireUserId()
        => _authentication.GetCurrentUserId(HttpContext)
           ?? throw new InvalidOperationException("当前用户会话无效。");

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "Asia/Shanghai" : id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
