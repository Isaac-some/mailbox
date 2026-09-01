using MailArchiver.Services;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers.Api;

[ApiController]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Api")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IQueryable<MailAccount> GetAllowedAccountsQuery(MailArchiverDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (User.IsInRole("Admin"))
            return context.MailAccounts;

        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId)
            ? context.MailAccounts.Where(account => account.UserMailAccounts.Any(link => link.UserId == userId))
            : context.MailAccounts.Where(_ => false);
    }

    protected int? GetCurrentUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var userId) ? userId : null;
    }

    protected async Task<List<int>> GetAllowedAccountIdsAsync()
    {
        var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
        var userService = HttpContext.RequestServices.GetService<IUserService>();

        if (authService == null || userService == null)
        {
            return new List<int>();
        }

        var username = authService.GetCurrentUserDisplayName(HttpContext);
        var user = await userService.GetUserByUsernameAsync(username);
        if (user == null)
        {
            return new List<int>();
        }

        var userAccounts = await userService.GetUserMailAccountsAsync(user.Id);
        return userAccounts.Select(a => a.Id).ToList();
    }
}
