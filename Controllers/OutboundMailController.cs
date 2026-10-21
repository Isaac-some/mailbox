using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Controllers;

[SelfManagerRequired]
public sealed class OutboundMailController : Controller
{
    private const int MaxSendingAccountOptions = 50;
    private readonly MailArchiverDbContext _context;
    private readonly IAuthenticationService _authentication;
    private readonly IOutboundMailService _outboundMail;
    private readonly OutboundMailOptions _options;
    private readonly ILogger<OutboundMailController> _logger;
    private readonly OutboundAccountPreparation? _preparation;

    public OutboundMailController(
        MailArchiverDbContext context,
        IAuthenticationService authentication,
        IOutboundMailService outboundMail,
        IOptions<OutboundMailOptions> options,
        ILogger<OutboundMailController> logger, OutboundAccountPreparation? preparation = null)
    {
        _context = context;
        _preparation = preparation;
        _authentication = authentication;
        _outboundMail = outboundMail;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? accountId, string? accountQuery, int? replyToEmailId = null)
    {
        if (!_options.Enabled)
            return NotFound();

        var model = new ComposeMailViewModel
        {
            AccountId = accountId ?? 0,
            AccountQuery = accountQuery?.Trim()
        };
        if (replyToEmailId.HasValue)
        {
            var userId = _authentication.GetCurrentUserId(HttpContext);
            if (!userId.HasValue) return NotFound();
            var isAdmin = await IsAdministratorAsync(userId.Value, HttpContext.RequestAborted);
            var original = await _context.ArchivedEmails.AsNoTracking()
                .Where(email => email.Id == replyToEmailId.Value
                    && (isAdmin || email.MailAccount.UserMailAccounts.Any(link => link.UserId == userId.Value)))
                .Select(email => new { email.MailAccountId, email.From, email.To, email.Subject, email.Body, email.IsOutgoing })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (original is null) return NotFound();
            model.AccountId = original.MailAccountId;
            var recipient = original.IsOutgoing ? original.To : original.From;
            if (InternetAddressList.TryParse(recipient, out var addresses))
                model.To = string.Join(", ", addresses.Mailboxes.Select(mailbox => mailbox.Address));
            model.Subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? original.Subject : "Re: " + original.Subject;
            model.Body = "\n\n----- 原邮件 -----\n" + original.Body;
        }
        await PopulateOptionsAsync(model, model.AccountQuery);
        if (model.AccountId > 0 && model.SendingAccounts.Any(item => item.Value == model.AccountId.ToString()))
            model.AccountIds = [model.AccountId];

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
    public async Task<IActionResult> Index(ComposeMailViewModel model, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return NotFound();

        var to = ParseRecipients(model.To, nameof(model.To));
        var cc = ParseRecipients(model.Cc, nameof(model.Cc));
        if (model.Subject?.Contains('\r') == true || model.Subject?.Contains('\n') == true)
            ModelState.AddModelError(nameof(model.Subject), "主题不能包含换行符。");
        ValidateAttachments(model);
        if (to?.Count != 1) ModelState.AddModelError(nameof(model.To), "请填写一个收件邮箱；多个收件人请使用发件任务。");
        var selectedIds = model.AccountIds.Count > 0 ? model.AccountIds.Distinct().ToList()
            : model.AccountId > 0 ? [model.AccountId] : new List<int>();
        model.AccountIds = selectedIds;
        if (selectedIds.Count is 0 or > 20) ModelState.AddModelError(nameof(model.AccountIds), "请选择 1–20 个发件账号。");

        var userId = _authentication.GetCurrentUserId(HttpContext);
        var isAdmin = userId.HasValue && await IsAdministratorAsync(userId.Value, HttpContext.RequestAborted);
        var accounts = userId.HasValue
            ? await _context.MailAccounts.Where(candidate => selectedIds.Contains(candidate.Id)
                && candidate.IsEnabled && candidate.Provider != ProviderType.IMPORT
                && (isAdmin || candidate.UserMailAccounts.Any(ownership => ownership.UserId == userId.Value)))
                .OrderBy(candidate => candidate.Id).ToListAsync(cancellationToken) : [];
        if (accounts.Count != selectedIds.Count)
            ModelState.AddModelError(nameof(model.AccountIds), "发件账号不存在、已停用或无权使用。");

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model);
            return View(model);
        }

        var attachments = new List<OutboundAttachment>();
        foreach (var file in model.Attachments?.Where(file => file.Length > 0) ?? [])
        {
            await using var stream = new MemoryStream((int)file.Length);
            await file.CopyToAsync(stream, cancellationToken);
            attachments.Add(new OutboundAttachment(
                Path.GetFileName(file.FileName),
                file.ContentType,
                stream.ToArray()));
        }

        var remaining = new List<int>();
        foreach (var account in accounts)
        {
            try
            {
                if (_preparation is not null) await _preparation.PrepareAsync(account, userId!.Value, cancellationToken, forceRefresh: false);
                var result = await _outboundMail.SendAsync(account,
                    new OutboundMailMessage(to!, cc!, model.Subject, model.Body, attachments), cancellationToken);
                model.SendResults.Add($"{account.EmailAddress}：" + (result.SentCopySaved
                    ? "已发送并保存到已发送。" : "已发出，副本保存失败，请勿重复发送。"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Outbound mail failed for account {AccountId}: {ExceptionType}", account.Id, ex.GetType().Name);
                remaining.Add(account.Id);
                ModelState.AddModelError(string.Empty, $"{account.EmailAddress}：{OutboundMailFailurePolicy.ToSafeMessage(ex)}");
            }
        }
        if (remaining.Count == 0)
        {
            TempData["SuccessMessage"] = string.Join(" ", model.SendResults);
            return RedirectToAction("Index", "Emails", new { SelectedAccountId = accounts[0].Id });
        }
        // Successful senders are removed so a retry cannot resend their mail.
        model.AccountId = 0;
        model.AccountIds = remaining;
        ModelState.Remove(nameof(model.AccountId));
        ModelState.Remove(nameof(model.AccountIds));
        await PopulateOptionsAsync(model);
        return View(model);
    }

    private IReadOnlyList<string>? ParseRecipients(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        try
        {
            var parsed = InternetAddressList.Parse(value.Replace(';', ','));
            var addresses = parsed.Mailboxes.Select(mailbox => mailbox.Address).ToList();
            if (addresses.Count == 0 || parsed.Count != addresses.Count
                || addresses.Any(address => !address.Contains('@')
                    || !System.Net.Mail.MailAddress.TryCreate(address, out _)))
                throw new FormatException();
            return addresses;
        }
        catch (ParseException)
        {
            ModelState.AddModelError(fieldName, "邮箱地址格式不正确，多个地址请用逗号分隔。");
            return null;
        }
        catch (FormatException)
        {
            ModelState.AddModelError(fieldName, "邮箱地址格式不正确，多个地址请用逗号分隔。");
            return null;
        }
    }

    private void ValidateAttachments(ComposeMailViewModel model)
    {
        var files = model.Attachments?.Where(file => file.Length > 0).ToList() ?? [];
        if (files.Count > _options.MaxAttachmentCount)
            ModelState.AddModelError(nameof(model.Attachments), $"附件最多 {_options.MaxAttachmentCount} 个。");
        if (files.Sum(file => file.Length) > _options.MaxTotalAttachmentBytes)
            ModelState.AddModelError(nameof(model.Attachments), $"附件总大小不能超过 {_options.MaxTotalAttachmentBytes / 1024 / 1024} MB。");
    }

    private async Task PopulateOptionsAsync(ComposeMailViewModel model, string? accountQuery = null)
    {
        var userId = _authentication.GetCurrentUserId(HttpContext);
        var normalizedQuery = accountQuery?.Trim();
        model.AccountQuery = normalizedQuery;

        if (!userId.HasValue)
        {
            model.SendingAccounts = [];
            return;
        }

        var isAdmin = await IsAdministratorAsync(userId.Value, HttpContext.RequestAborted);
        var query = _context.MailAccounts
            .AsNoTracking()
            .Where(account =>
                account.IsEnabled
                && account.Provider != ProviderType.IMPORT
                && (isAdmin
                    || account.UserMailAccounts.Any(ownership => ownership.UserId == userId.Value))
                );

        if (model.AccountIds.Count > 0)
        {
            query = query.Where(account => model.AccountIds.Contains(account.Id));
        }
        else if (model.AccountId > 0)
        {
            query = query.Where(account => account.Id == model.AccountId);
        }
        else if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var search = normalizedQuery.ToLower();
            query = query.Where(account =>
                account.EmailAddress.ToLower().Contains(search) ||
                account.Name.ToLower().Contains(search));
        }

        // Provider capability inspection is local but not SQL-translatable. Read a
        // bounded candidate window and render at most one page of select options.
        var candidateLimit = model.AccountId > 0 ? 1 : MaxSendingAccountOptions * 4 + 1;
        var accounts = await query
            .OrderBy(account => account.EmailAddress)
            .Take(candidateLimit)
            .ToListAsync();
        var sendingAccounts = accounts.Take(MaxSendingAccountOptions + 1).ToList();

        model.HasMoreSendingAccounts = sendingAccounts.Count > MaxSendingAccountOptions
            || (model.AccountId == 0 && accounts.Count == candidateLimit);
        model.SendingAccounts = sendingAccounts
            .Take(MaxSendingAccountOptions)
            .Select(account => new SelectListItem(account.EmailAddress, account.Id.ToString()))
            .ToList();
        model.MaxAttachmentCount = _options.MaxAttachmentCount;
        model.MaxTotalAttachmentBytes = _options.MaxTotalAttachmentBytes;
    }

    private async Task<bool> IsAdministratorAsync(int userId, CancellationToken cancellationToken)
        => _authentication.IsCurrentUserAdmin(HttpContext)
            || await _context.Users.AsNoTracking()
                .Where(user => user.Id == userId && user.IsAdmin && user.IsActive)
                .Select(user => true)
                .FirstOrDefaultAsync(cancellationToken);
}
