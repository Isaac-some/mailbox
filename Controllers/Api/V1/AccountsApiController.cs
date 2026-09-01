using MailArchiver.Controllers.Api;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.Api;
using MailArchiver.Services.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Controllers.Api.V1;

[Route("api/v1/accounts")]
public class AccountsApiController : ApiControllerBase
{
    private readonly MailArchiverDbContext _context;
    private readonly EmailCoreService _emailCoreService;
    private readonly ApiOptions _options;

    public AccountsApiController(
        MailArchiverDbContext context,
        EmailCoreService emailCoreService,
        IOptions<ApiOptions> options)
    {
        _context = context;
        _emailCoreService = emailCoreService;
        _options = options.Value;
    }

    [HttpGet("")]
    public async Task<ActionResult<PagedResultDto<MailAccountDto>>> GetAccounts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 0)
    {
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? _options.DefaultPageSize : pageSize;
        pageSize = Math.Clamp(pageSize, 1, _options.MaxPageSize);
        // Keep permission filtering in SQL. Do not materialize every account ID
        // before applying the page window when a tenant has a large inventory.
        var accountsQuery = GetAllowedAccountsQuery(_context);

        var totalItems = await accountsQuery.CountAsync();
        var accounts = await accountsQuery
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new PagedResultDto<MailAccountDto>
        {
            Items = accounts.Select(MailAccountDto.FromEntity).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
        });
    }

    [HttpGet("{id:int}/folders")]
    public async Task<ActionResult<List<FolderNodeDto>>> GetFolders(int id)
    {
        var canAccess = await GetAllowedAccountsQuery(_context).AnyAsync(account => account.Id == id);
        if (!canAccess)
        {
            return NotFound();
        }

        var accountExists = await _context.MailAccounts.AnyAsync(a => a.Id == id);
        if (!accountExists)
        {
            return NotFound();
        }

        // The account was already authorized in SQL; pass only this ID to the
        // service so folder aggregation remains bounded to the requested account.
        var folders = await _emailCoreService.GetFolderTreeAsync(id, new List<int> { id });
        return Ok(folders.Select(FolderNodeDto.FromNode).ToList());
    }
}
