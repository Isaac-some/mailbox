using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MailArchiver.Attributes;

/// <summary>
/// Final HTTP-layer guard for the local archive. UI hiding is not a security
/// control, so every archive deletion and every mailbox-write endpoint is
/// rejected even when a caller crafts the URL directly.
/// </summary>
public sealed class StrictReadOnlyArchiveFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> EmailWriteActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Restore", "BatchRestore", "BatchRestoreStart", "StartAsyncBatchRestore",
        "Delete", "DeleteSelected"
    };

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = context.Controller.GetType().Name;
        var action = context.ActionDescriptor.RouteValues.TryGetValue("action", out var routeAction)
            ? routeAction
            : string.Empty;

        var isBlocked = controller == "EmailsController" && EmailWriteActions.Contains(action ?? string.Empty)
            || controller == "MailAccountsController" && string.Equals(action, "Delete", StringComparison.OrdinalIgnoreCase);

        if (isBlocked)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return Task.CompletedTask;
        }

        return next();
    }
}
