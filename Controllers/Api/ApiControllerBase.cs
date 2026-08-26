using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers.Api;

[ApiController]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Api")]
public abstract class ApiControllerBase : ControllerBase
{
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
