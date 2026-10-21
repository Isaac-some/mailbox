using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailArchiver.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers;

public sealed class MailDomainsController(IMailDomainProfileStore profiles, IConfiguration configuration) : Controller
{
    private bool CanManage()
    {
        var auth = HttpContext.RequestServices.GetService<IAuthenticationService>();
        return auth?.GetCurrentUserId(HttpContext) is not null &&
            (configuration.GetValue<bool>("LocalApp:Enabled")
            || Environment.GetEnvironmentVariable("MAIL_ASSISTANT_LOCAL_APP") == "1"
            || auth.IsCurrentUserAdmin(HttpContext));
    }

    [HttpGet]
    public IActionResult Index(string? domain = null)
    {
        if (!CanManage()) return Forbid();
        var model = new MailDomainSettingsViewModel { Profiles = profiles.GetAll() };
        if (!string.IsNullOrWhiteSpace(domain))
        {
            try
            {
                model.Domain = MailDomainProfileStore.NormalizeDomain(domain);
                if (profiles.Find(model.Domain) is { } saved)
                {
                    model.IncomingHost = saved.Incoming?.Host ?? string.Empty;
                    model.IncomingPort = saved.Incoming?.Port ?? 993;
                    model.IncomingImplicitTls = saved.Incoming?.ImplicitTls ?? true;
                    model.IncomingUsername = saved.Incoming?.Username ?? "%EMAILADDRESS%";
                    model.OutgoingHost = saved.Outgoing?.Host ?? string.Empty;
                    model.OutgoingPort = saved.Outgoing?.Port ?? 587;
                    model.OutgoingImplicitTls = saved.Outgoing?.ImplicitTls ?? false;
                    model.OutgoingUsername = saved.Outgoing?.Username ?? "%EMAILADDRESS%";
                    model.OAuthTokenEndpoint = saved.OAuth?.TokenEndpoint;
                    model.OAuthRequiresSecret = saved.OAuth?.RequiresClientSecret ?? false;
                    model.OAuthRequiresRedirect = saved.OAuth?.RequiresRedirectUri ?? false;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { ModelState.AddModelError(nameof(model.Domain), "域名格式不正确。"); }
        }
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(MailDomainSettingsViewModel model, CancellationToken cancellationToken)
    {
        if (!CanManage()) return Forbid();
        if (ModelState.IsValid)
        {
            try
            {
                ExternalOAuthSettings? oauth = string.IsNullOrWhiteSpace(model.OAuthTokenEndpoint) ? null
                    : new("域名邮箱", model.OAuthTokenEndpoint.Trim(), model.OAuthRequiresSecret, model.OAuthRequiresRedirect);
                await profiles.SaveConfigurationAsync(new(model.Domain,
                    new(model.IncomingHost.Trim().ToLowerInvariant(), model.IncomingPort, model.IncomingImplicitTls, model.IncomingUsername),
                    new(model.OutgoingHost.Trim().ToLowerInvariant(), model.OutgoingPort, model.OutgoingImplicitTls, model.OutgoingUsername),
                    "Manual", OAuth: oauth), cancellationToken);
                TempData["SuccessMessage"] = "域名连接设置已保存，同域名邮箱下次连接时自动使用；每个账号仍需单独验证授权。";
                return RedirectToAction(nameof(Index), new { domain = model.Domain });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { ModelState.AddModelError(string.Empty, "设置无效，请检查公网域名、服务器地址、端口和 OAuth HTTPS 地址。"); }
        }
        model.Profiles = profiles.GetAll();
        return View("Index", model);
    }
}
