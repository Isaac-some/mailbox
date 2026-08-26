using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Controllers
{
    [UserAccessRequired]
    public class LogsController : Controller
    {
        private readonly IAccessLogService _accessLogService;
        private readonly IAuthenticationService _authenticationService;
        private readonly MailArchiverDbContext _context;

        public LogsController(IAccessLogService accessLogService, IAuthenticationService authenticationService, MailArchiverDbContext context)
        {
            _accessLogService = accessLogService;
            _authenticationService = authenticationService;
            _context = context;
        }

        public async Task<IActionResult> Index(int page = 1, int pageSize = 50, DateTime? fromDate = null, DateTime? toDate = null, string username = null, AccessLogType? type = null)
        {
            var currentUsername = _authenticationService.GetCurrentUserDisplayName(HttpContext);
            var isAdmin = _authenticationService.IsCurrentUserAdmin(HttpContext);

            // Set default page size to 50
            pageSize = 50;

            var logs = await _accessLogService.GetLogsForUserAsync(currentUsername, fromDate, toDate);

            // Filter by type if specified
            if (type.HasValue)
            {
                logs = logs.Where(l => l.Type == type.Value).ToList();
            }

            // Order by timestamp descending (newest first) - already done in service

            // Implement pagination
            var totalLogs = logs.Count;
            var totalPages = (int)Math.Ceiling((double)totalLogs / pageSize);
            var paginatedLogs = logs.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.PageSize = pageSize;
            ViewBag.IsAdmin = false;
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate = toDate;
            ViewBag.UsernameFilter = username;
            ViewBag.TypeFilter = type;

            return View(paginatedLogs);
        }
    }
}
