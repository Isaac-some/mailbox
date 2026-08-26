using MailArchiver.Attributes;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers;

[AdminRequired]
public sealed class RegistrationCodesController : Controller
{
    private readonly IRegistrationCodeService _registrationCodes;
    private readonly IAuthenticationService _authentication;

    public RegistrationCodesController(
        IRegistrationCodeService registrationCodes,
        IAuthenticationService authentication)
    {
        _registrationCodes = registrationCodes;
        _authentication = authentication;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _registrationCodes.GetAllAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int validDays = 7)
    {
        var userId = _authentication.GetCurrentUserId(HttpContext);
        if (!userId.HasValue)
        {
            return Forbid();
        }

        TempData["NewRegistrationCode"] =
            await _registrationCodes.CreateAsync(userId.Value, validDays);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id)
    {
        if (!await _registrationCodes.RevokeAsync(id))
        {
            return NotFound();
        }

        TempData["SuccessMessage"] = "授权码已撤销";
        return RedirectToAction(nameof(Index));
    }
}
