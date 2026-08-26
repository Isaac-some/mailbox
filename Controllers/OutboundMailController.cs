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
    private readonly MailArchiverDbContext _context;
    private readonly IAuthenticationService _authentication;
    private readonly IOutboundMailService _outboundMail;
    private readonly OutboundMailOptions _options;
    private readonly ILogger<OutboundMailController> _logger;

    public OutboundMailController(
        MailArchiverDbContext context,
        IAuthenticationService authentication,
        IOutboundMailService outboundMail,
        IOptions<OutboundMailOptions> options,
        ILogger<OutboundMailController> logger)
    {
        _context = context;
        _authentication = authentication;
        _outboundMail = outboundMail;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? accountId)
    {
        if (!_options.Enabled)
            return NotFound();

        var model = new ComposeMailViewModel { AccountId = accountId ?? 0 };
        await PopulateOptionsAsync(model);
        if (model.SendingAccounts.Count == 0)
        {
            TempData["ErrorMessage"] = "没有可发件的账号。Gmail/Yahoo 可用 OAuth 或应用专用密码，GMX 使用应用专用密码，Outlook 使用 OAuth。";
            return RedirectToAction("Index", "MailAccounts");
        }

        if (model.AccountId == 0 || model.SendingAccounts.All(item => item.Value != model.AccountId.ToString()))
            model.AccountId = int.Parse(model.SendingAccounts[0].Value!);

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
        if (model.Subject.Contains('\r') || model.Subject.Contains('\n'))
            ModelState.AddModelError(nameof(model.Subject), "主题不能包含换行符。");
        ValidateAttachments(model);

        var userId = _authentication.GetCurrentUserId(HttpContext);
        var account = userId.HasValue
            ? await _context.MailAccounts
                .FirstOrDefaultAsync(candidate =>
                    candidate.Id == model.AccountId
                    && candidate.IsEnabled
                    && candidate.Provider != ProviderType.IMPORT
                    && candidate.UserMailAccounts.Any(ownership => ownership.UserId == userId.Value),
                    cancellationToken)
            : null;

        if (account is null)
            ModelState.AddModelError(nameof(model.AccountId), "发件账号不存在、已停用或无权使用。");
        else if (!_outboundMail.CanSend(account))
            ModelState.AddModelError(nameof(model.AccountId), "该账号没有可用的发件凭据。");

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model);
            return View(model);
        }

        var attachments = new List<OutboundAttachment>();
        foreach (var file in model.Attachments.Where(file => file.Length > 0))
        {
            await using var stream = new MemoryStream((int)file.Length);
            await file.CopyToAsync(stream, cancellationToken);
            attachments.Add(new OutboundAttachment(
                Path.GetFileName(file.FileName),
                file.ContentType,
                stream.ToArray()));
        }

        try
        {
            var result = await _outboundMail.SendAsync(
                account!,
                new OutboundMailMessage(to!, cc!, model.Subject, model.Body, attachments),
                cancellationToken);

            TempData[result.SentCopySaved ? "SuccessMessage" : "ErrorMessage"] = result.SentCopySaved
                ? "邮件已发送，并已保存到“已发送”。"
                : "邮件已经发送，但“已发送”副本保存失败，请勿重复发送。";
            return RedirectToAction("Index", "Emails", new { SelectedAccountId = account!.Id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbound mail failed before completion for account {AccountId}", model.AccountId);
            ModelState.AddModelError(string.Empty, "发送失败，邮件尚未确认发出。请检查授权信息、应用密码和网络后重试。");
            await PopulateOptionsAsync(model);
            return View(model);
        }
    }

    private IReadOnlyList<string>? ParseRecipients(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        try
        {
            var parsed = InternetAddressList.Parse(value.Replace(';', ','));
            var addresses = parsed.Mailboxes.Select(mailbox => mailbox.Address).ToList();
            if (addresses.Count == 0 || parsed.Count != addresses.Count)
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
        var files = model.Attachments.Where(file => file.Length > 0).ToList();
        if (files.Count > _options.MaxAttachmentCount)
            ModelState.AddModelError(nameof(model.Attachments), $"附件最多 {_options.MaxAttachmentCount} 个。");
        if (files.Sum(file => file.Length) > _options.MaxTotalAttachmentBytes)
            ModelState.AddModelError(nameof(model.Attachments), $"附件总大小不能超过 {_options.MaxTotalAttachmentBytes / 1024 / 1024} MB。");
    }

    private async Task PopulateOptionsAsync(ComposeMailViewModel model)
    {
        var userId = _authentication.GetCurrentUserId(HttpContext);
        var accounts = userId.HasValue
            ? await _context.MailAccounts
                .AsNoTracking()
                .Where(account =>
                    account.IsEnabled
                    && account.Provider != ProviderType.IMPORT
                    && account.UserMailAccounts.Any(ownership => ownership.UserId == userId.Value))
                .OrderBy(account => account.EmailAddress)
                .ToListAsync()
            : new List<MailAccount>();

        model.SendingAccounts = accounts
            .Where(_outboundMail.CanSend)
            .Select(account => new SelectListItem(account.EmailAddress, account.Id.ToString()))
            .ToList();
        model.MaxAttachmentCount = _options.MaxAttachmentCount;
        model.MaxTotalAttachmentBytes = _options.MaxTotalAttachmentBytes;
    }
}
