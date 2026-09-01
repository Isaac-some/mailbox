using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.ViewModels;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailArchiver.Services.Providers;
using MailArchiver.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using System.Security.Cryptography;
using System.Text;

using MailArchiver.Attributes;

namespace MailArchiver.Controllers
{
    [SelfManagerRequired]
    public class MailAccountsController : Controller
    {
    private const int MaxAccountImportFiles = 20;
    private readonly MailArchiverDbContext _context;
    private readonly MailArchiver.Services.Core.EmailCoreService _emailCoreService;
    private readonly MailArchiver.Services.Factories.ProviderEmailServiceFactory _providerFactory;
    private readonly IGraphEmailService _graphEmailService;
    private readonly ILogger<MailAccountsController> _logger;
    private readonly BatchRestoreOptions _batchOptions;
    private readonly TenantManagementOptions _tenantManagementOptions;
    private readonly ISyncJobService _syncJobService;
    private readonly IMBoxImportService _mboxImportService;
    private readonly IEmlImportService _emlImportService;
    private readonly UploadOptions _uploadOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IExportService _exportService;
    private readonly IAccessLogService _accessLogService;
    private readonly IMailAccountDeletionService _mailAccountDeletionService;
    private readonly IMsaOAuthService _msaOAuthService;
    private readonly MsaOAuthOptions _msaOptions;
    private readonly IAccountStorageService _accountStorageService;
    private readonly CsvImportOptions _csvImportOptions;
    private readonly ICredentialEncryptionService _credentialEncryptionService;
    private readonly IOnDemandMailSyncQueue _onDemandSyncQueue;
    private readonly IMailProviderRegistry _mailProviderRegistry;

    public MailAccountsController(
        MailArchiverDbContext context,
        MailArchiver.Services.Core.EmailCoreService emailCoreService,
        MailArchiver.Services.Factories.ProviderEmailServiceFactory providerFactory,
        IGraphEmailService graphEmailService,
        ILogger<MailAccountsController> logger,
        IOptions<BatchRestoreOptions> batchOptions,
        IOptions<TenantManagementOptions> tenantManagementOptions,
        ISyncJobService syncJobService,
        IMBoxImportService mboxImportService,
        IEmlImportService emlImportService,
        IOptions<UploadOptions> uploadOptions,
        IStringLocalizer<SharedResource> localizer,
        IServiceScopeFactory serviceScopeFactory,
        IExportService exportService,
        IAccessLogService accessLogService,
        IMailAccountDeletionService mailAccountDeletionService,
        IMsaOAuthService msaOAuthService,
        IOptions<MsaOAuthOptions> msaOptions,
        IAccountStorageService accountStorageService,
        IOptions<CsvImportOptions> csvImportOptions,
        ICredentialEncryptionService credentialEncryptionService,
        IOnDemandMailSyncQueue onDemandSyncQueue,
        IMailProviderRegistry mailProviderRegistry)
    {
        _context = context;
        _emailCoreService = emailCoreService;
        _providerFactory = providerFactory;
        _graphEmailService = graphEmailService;
        _logger = logger;
        _batchOptions = batchOptions.Value;
        _tenantManagementOptions = tenantManagementOptions.Value;
        _syncJobService = syncJobService;
        _mboxImportService = mboxImportService;
        _emlImportService = emlImportService;
        _uploadOptions = uploadOptions.Value;
        _localizer = localizer;
        _serviceScopeFactory = serviceScopeFactory;
        _exportService = exportService;
        _accessLogService = accessLogService;
        _mailAccountDeletionService = mailAccountDeletionService;
        _msaOAuthService = msaOAuthService;
        _msaOptions = msaOptions.Value;
        _accountStorageService = accountStorageService;
        _csvImportOptions = csvImportOptions.Value;
        _credentialEncryptionService = credentialEncryptionService;
        _onDemandSyncQueue = onDemandSyncQueue;
        _mailProviderRegistry = mailProviderRegistry;
    }

        private async Task<bool> HasAccessToAccountAsync(int accountId)
        {
            var authService = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
            var userId = authService.GetCurrentUserId(HttpContext);
            return await MailAccountManagementScope.Apply(
                    _context.MailAccounts,
                    userId,
                    authService.IsCurrentUserAdmin(HttpContext))
                .AnyAsync(account => account.Id == accountId);
        }

        private IQueryable<MailAccount> ManageableMailAccounts()
        {
            var authService = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
            var userId = authService.GetCurrentUserId(HttpContext);
            return MailAccountManagementScope.Apply(
                _context.MailAccounts,
                userId,
                authService.IsCurrentUserAdmin(HttpContext));
        }

        // GET: MailAccounts
        public async Task<IActionResult> Index(string? q, int page = 1, int pageSize = 50)
        {
            // Use the authentication service to get user info properly
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
            IQueryable<MailAccount> mailAccountsQuery = ManageableMailAccounts();

            var pendingDeletionIds = _mailAccountDeletionService
                .GetAllJobs()
                .Where(job => job.Status is MailAccountDeletionJobStatus.Queued or MailAccountDeletionJobStatus.Running)
                .Select(job => job.MailAccountId)
                .Distinct()
                .ToList();
            if (pendingDeletionIds.Count > 0)
            {
                mailAccountsQuery = mailAccountsQuery
                    .Where(account => !pendingDeletionIds.Contains(account.Id));
            }

            q = q?.Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var search = q.ToLower();
                mailAccountsQuery = mailAccountsQuery.Where(a =>
                    a.Name.ToLower().Contains(search) ||
                    a.EmailAddress.ToLower().Contains(search));
            }

            pageSize = pageSize is 25 or 50 or 100 ? pageSize : 50;
            var totalCount = await mailAccountsQuery.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var accountRows = await mailAccountsQuery
                .AsNoTracking()
                .OrderBy(a => a.Name)
                .ThenBy(a => a.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(a => new
                {
                    Account = a,
                    ArchivedEmailCount = a.ArchivedEmails.Count()
                })
                .ToListAsync();

            var accounts = accountRows.Select(row =>
            {
                var a = row.Account;
                MailAccountCapabilities? capabilities = null;
                IMailProviderModule? module = null;
                if (a.MailProviderKind is not null)
                {
                    module = _mailProviderRegistry.For(a);
                    capabilities = module.Inspect(a);
                }

                return new MailAccountViewModel
                {
                    Id = a.Id,
                    Name = a.Name,
                    EmailAddress = a.EmailAddress,
                    GroupName = a.GroupName,
                    ImapServer = a.ImapServer,
                    ImapPort = a.ImapPort,
                    Username = a.Username,
                    UseSSL = a.UseSSL,
                    IsEnabled = a.IsEnabled,
                    LastSync = a.LastSync,
                    DeleteAfterDays = a.DeleteAfterDays,
                    Provider = a.Provider,
                    MailProviderKind = a.MailProviderKind,
                    ProviderLabel = module?.DisplayName ?? a.Provider.ToString(),
                    MsaIsAuthorized = a.MailProviderKind == MailProviderKind.Outlook && capabilities?.CanReceive == true,
                    MsaCanSend = a.MailProviderKind == MailProviderKind.Outlook && capabilities?.CanSend == true,
                    CanReceive = capabilities?.CanReceive == true,
                    CanSend = capabilities?.CanSend == true,
                    RequiredAction = capabilities?.RequiredAction,
                    ArchivedEmailCount = row.ArchivedEmailCount
                };
            }).ToList();

            _logger.LogInformation("Returning {Count} accounts for user {Username}", accounts.Count, currentUsername);

            ViewBag.AccountSearch = q ?? string.Empty;
            ViewBag.AccountPage = page;
            ViewBag.AccountPageSize = pageSize;
            ViewBag.AccountTotalCount = totalCount;
            ViewBag.AccountTotalPages = totalPages;

            return View(accounts);
        }

        // GET: MailAccounts/Details/5
        public async Task<IActionResult> Details(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // E-Mail-Anzahl abrufen
            var emailCount = await _emailCoreService.GetEmailCountByAccountAsync(id);

            var storageMap = await _accountStorageService.GetStorageForAccountsAsync(new List<int> { id });
            var storageUsed = storageMap.TryGetValue(id, out var storage)
                ? storage
                : AccountStorageService.FormatFileSize(0);

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress,
                GroupName = account.GroupName,
                ImapServer = account.ImapServer,
                ImapPort = account.ImapPort,
                Username = account.Username,
                UseSSL = account.UseSSL,
                LastSync = account.LastSync,
                IsEnabled = account.IsEnabled,
                DeleteAfterDays = account.DeleteAfterDays,
                Provider = account.Provider,
                MailProviderKind = account.MailProviderKind,
                ProviderLabel = account.MailProviderKind is null
                    ? account.Provider.ToString()
                    : _mailProviderRegistry.For(account).DisplayName,
                ClientId = account.ClientId,
                TenantId = account.TenantId,
                ExcludedFolders = account.ExcludedFolders,
                LocalRetentionDays = account.LocalRetentionDays,
                StorageUsed = storageUsed,
                MsaClientId = account.Provider == ProviderType.MSA ? account.ClientId : null,
                MsaIsAuthorized = account.Provider == ProviderType.MSA && !string.IsNullOrEmpty(account.OAuthRefreshToken),
                MsaTokenExpiry = account.OAuthTokenExpiry,
                ArchivedEmailCount = emailCount,
            };

            ViewBag.EmailCount = emailCount;
            return View(model);
        }

        // GET: MailAccounts/Create
        public IActionResult Create()
        {
            ViewBag.MsaHasDefaultClientId = _msaOptions.HasDefaultClientId;
            var model = new CreateMailAccountViewModel
            {
                ImapPort = 993, // Standard values
                UseSSL = true,
                Provider = ProviderType.IMAP,
                ImportEntireTenant = true,
                ImportAllTenantMailboxes = true,
                SkipDisabledMailboxes = true
            };
            return View(model);
        }

        // POST: MailAccounts/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateMailAccountViewModel model)
        {
            IMailProviderModule? mailProviderModule = null;
            var isLocalApp = string.Equals(
                Environment.GetEnvironmentVariable("KOUZI_LOCAL_APP"),
                "1",
                StringComparison.Ordinal);

            if (isLocalApp)
            {
                // The desktop app has one account entry. Provider is derived on the
                // server so a stale or manipulated hidden field can never select the
                // wrong authorization and mail module.
                ModelState.Clear();
                try
                {
                    mailProviderModule = _mailProviderRegistry.Detect(model.EmailAddress ?? string.Empty);
                    model.Provider = mailProviderModule.Kind == MailProviderKind.Outlook
                        ? ProviderType.MSA
                        : ProviderType.IMAP;
                    var endpoint = mailProviderModule.GetIncomingEndpoint(new MailAccount { EmailAddress = model.EmailAddress! });
                    model.ImapServer = endpoint.Host;
                    model.ImapPort = endpoint.Port;
                    model.UseSSL = endpoint.UseSsl;
                    model.Username = model.EmailAddress!.Trim();
                    model.Name = MailAccountNamePolicy.Derive(model.EmailAddress!);
                    TryValidateModel(model);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                {
                    ModelState.AddModelError(nameof(model.EmailAddress), ex.Message);
                }
            }
            else if (model.Provider == ProviderType.IMAP)
            {
                try
                {
                    mailProviderModule = _mailProviderRegistry.Detect(model.EmailAddress ?? string.Empty);
                    if (mailProviderModule.Kind == MailProviderKind.Outlook)
                        throw new NotSupportedException("Outlook 请使用 Outlook 个人邮箱授权方式。");
                    var endpoint = mailProviderModule.GetIncomingEndpoint(new MailAccount { EmailAddress = model.EmailAddress! });
                    model.ImapServer = endpoint.Host;
                    model.ImapPort = endpoint.Port;
                    model.UseSSL = endpoint.UseSsl;
                    model.Username = model.EmailAddress!.Trim();
                    model.Name = MailAccountNamePolicy.Derive(model.EmailAddress!);
                    ModelState.Remove(nameof(model.Name));
                    ModelState.Remove(nameof(model.ImapServer));
                    ModelState.Remove(nameof(model.ImapPort));
                    ModelState.Remove(nameof(model.Username));
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                {
                    ModelState.AddModelError(nameof(model.EmailAddress), ex.Message);
                }
            }
            else if (model.Provider == ProviderType.MSA)
            {
                mailProviderModule = _mailProviderRegistry.For(MailProviderKind.Outlook);
                var endpoint = mailProviderModule.GetIncomingEndpoint(new MailAccount { EmailAddress = model.EmailAddress ?? string.Empty });
                model.ImapServer = endpoint.Host;
                model.ImapPort = endpoint.Port;
                model.UseSSL = endpoint.UseSsl;
                model.Username = model.EmailAddress?.Trim();
                model.Name = MailAccountNamePolicy.Derive(model.EmailAddress ?? string.Empty);
                ModelState.Remove(nameof(model.Name));
            }
            else
            {
                ModelState.AddModelError(nameof(model.Provider), "本地版目前仅支持 Gmail、Yahoo、GMX 和 Outlook 个人邮箱。");
            }
            model.DeleteAfterDays = null;
            model.LocalRetentionDays = null;

            if (mailProviderModule is not null && model.Provider == ProviderType.IMAP &&
                !string.IsNullOrWhiteSpace(model.Password))
            {
                try
                {
                    model.Password = mailProviderModule.NormalizeAppPassword(model.Password);
                }
                catch (InvalidOperationException ex)
                {
                    ModelState.AddModelError(nameof(model.Password), ex.Message);
                }
            }

            // When a default MSA ClientId is configured, per-account ClientId is optional.
            // When it is NOT configured, the per-account ClientId is required (validated below).
            if (model.Provider == ProviderType.MSA && !_msaOptions.HasDefaultClientId
                && string.IsNullOrWhiteSpace(model.MsaClientId))
            {
                ModelState.AddModelError("MsaClientId",
                    _localizer["MsaClientIdRequired"].Value);
            }

            ViewBag.MsaHasDefaultClientId = _msaOptions.HasDefaultClientId;

            if (ModelState.IsValid)
            {
                if (model.Provider == ProviderType.M365 && model.ImportEntireTenant)
                {
                    return await CreateM365TenantAccountsAsync(model);
                }

                var account = new MailAccount
                {
                    Name = model.Name,
                    EmailAddress = model.EmailAddress,
                    ImapServer = model.ImapServer,
                    ImapPort = model.ImapPort,
                    Username = model.Username,
                    Password = model.Provider == ProviderType.IMAP && !string.IsNullOrWhiteSpace(model.Password)
                        ? _credentialEncryptionService.Encrypt(model.Password)
                        : null,
                    UseSSL = model.UseSSL,
                    IsEnabled = model.IsEnabled,
                    Provider = model.Provider,
                    // For MSA: store the per-account ClientId only when one was entered.
                    // When empty, the MsaOAuthService resolves the configured default at runtime.
                    ClientId = model.Provider switch
                    {
                        ProviderType.MSA when !string.IsNullOrWhiteSpace(model.MsaClientId) => model.MsaClientId.Trim(),
                        ProviderType.IMAP when !string.IsNullOrWhiteSpace(model.ExternalOAuthClientId) => model.ExternalOAuthClientId.Trim(),
                        _ => null
                    },
                    ClientSecret = model.Provider switch
                    {
                        ProviderType.MSA when !string.IsNullOrWhiteSpace(model.MsaClientSecret) => model.MsaClientSecret,
                        ProviderType.IMAP when !string.IsNullOrWhiteSpace(model.ExternalOAuthClientSecret) => model.ExternalOAuthClientSecret,
                        _ => null
                    },
                    OAuthRefreshToken = model.Provider == ProviderType.IMAP && !string.IsNullOrWhiteSpace(model.OAuthRefreshToken)
                        ? model.OAuthRefreshToken.Trim()
                        : null,
                    OAuthRedirectUri = model.Provider == ProviderType.IMAP && !string.IsNullOrWhiteSpace(model.ExternalOAuthRedirectUri)
                        ? model.ExternalOAuthRedirectUri.Trim()
                        : null,
                    TenantId = null,
                    ExcludedFolders = string.Empty,
                    DeleteAfterDays = null,
                    LocalRetentionDays = null,
                    SyncIntervalMinutes = model.SyncIntervalMinutes,
                    FullSyncIntervalHours = model.FullSyncIntervalHours,
                    LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                };
                mailProviderModule!.PrepareAccount(account);

                // Validate local retention policy
                if (account.LocalRetentionDays.HasValue && !account.DeleteAfterDays.HasValue)
                {
                    ModelState.AddModelError("LocalRetentionDays", 
                        _localizer["LocalRetentionRequiresServerRetention"].Value);
                    return View(model);
                }
                
                if (account.LocalRetentionDays.HasValue && account.DeleteAfterDays.HasValue &&
                    account.LocalRetentionDays.Value < account.DeleteAfterDays.Value)
                {
                    ModelState.AddModelError("LocalRetentionDays", 
                        _localizer["LocalRetentionMustBeGreaterOrEqual"].Value);
                    return View(model);
                }

                // SECURITY: If the user chose to copy credentials from an existing M365 account,
                // copy the client secret server-side so it never travels to the browser.
                if (model.Provider == ProviderType.M365 &&
                    model.CopyCredentialsFromAccountId.HasValue &&
                    string.IsNullOrWhiteSpace(model.ClientSecret))
                {
                    var sourceAccountId = model.CopyCredentialsFromAccountId.Value;
                    if (await HasAccessToAccountAsync(sourceAccountId))
                    {
                        var sourceAccount = await _context.MailAccounts.FindAsync(sourceAccountId);
                        if (sourceAccount != null && sourceAccount.Provider == ProviderType.M365)
                        {
                            account.ClientSecret = sourceAccount.ClientSecret;
                            _logger.LogInformation(
                                "Copying M365 client secret from account {SourceAccountId} to new account {NewAccountEmail}",
                                sourceAccountId, account.EmailAddress);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Cannot copy M365 credentials: source account {SourceAccountId} not found or not M365",
                                sourceAccountId);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "User attempted to copy M365 credentials from account {SourceAccountId} without access",
                            sourceAccountId);
                    }
                }

                try
                {
                    _logger.LogInformation("Creating new account: {Name}, Provider: {Provider}",
                        model.Name, model.Provider);

                    // Test connection before saving (only for IMAP; M365 and MSA use OAuth and can't be tested this way)
                    if (account.Provider == ProviderType.IMAP)
                    {
                        _logger.LogInformation("Testing connection for account: {Name}, Server: {Server}:{Port}",
                            model.Name, model.ImapServer, model.ImapPort);
                        var imapService = HttpContext.RequestServices.GetService<MailArchiver.Services.Providers.ImapEmailService>();
                        var connectionResult = await imapService.TestConnectionAsync(account);
                        if (!connectionResult)
                        {
                            _logger.LogWarning("Connection test failed for account {Name}", model.Name);
                            ModelState.AddModelError("", _localizer["EmailAccountError"]);
                            return View(model);
                        }
                    }

                    _logger.LogInformation("Saving account to database");
                    _context.MailAccounts.Add(account);
                    await _context.SaveChangesAsync();
                    if (account.IsEnabled && account.Provider == ProviderType.IMAP)
                    {
                        _onDemandSyncQueue.Enqueue(account.Id, MailSyncRequestPriority.Bulk, MailSyncRequestKind.ValidateConnection);
                    }

                    // Every account is owned by the user who adds it.
                    var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                    var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                    var currentUser = await _context.Users
                        .FirstOrDefaultAsync(u => u.Username.ToLower() == currentUsername.ToLower());
                    
                    if (currentUser != null)
                    {
                        var userMailAccount = new UserMailAccount
                        {
                            UserId = currentUser.Id,
                            MailAccountId = account.Id
                        };
                        _context.UserMailAccounts.Add(userMailAccount);
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Assigned account {AccountName} to owner {Username}",
                            account.Name, currentUser.Username);
                    }

                    // Log the account creation action
                    if (!string.IsNullOrEmpty(currentUsername))
                    {
                        await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                            searchParameters: $"Created mail account: {account.Name}",
                            mailAccountId: account.Id);
                    }
                    
                    TempData["SuccessMessage"] = _localizer["EmailAccountCreateSuccess"].Value;

                    // For MSA accounts, redirect directly to the device-code authorization page
                    // so the user can authenticate immediately — no Edit round-trip needed.
                    if (account.Provider == ProviderType.MSA)
                    {
                        return RedirectToAction(nameof(AuthorizeMsaDevice), new { id = account.Id });
                    }

                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating email account: {Message}", ex.Message);
                    ModelState.AddModelError("", $"{_localizer["ErrorOccurred"]}: {ex.Message}");
                    return View(model);
                }
            }

            // Wenn ModelState ungültig ist, zurück zur Ansicht mit Fehlern
            return View(model);
        }

        private async Task<IActionResult> CreateM365TenantAccountsAsync(CreateMailAccountViewModel model)
        {
            if (model.LocalRetentionDays.HasValue && !model.DeleteAfterDays.HasValue)
            {
                ModelState.AddModelError("LocalRetentionDays",
                    _localizer["LocalRetentionRequiresServerRetention"].Value);
                return View("Create", model);
            }

            if (model.LocalRetentionDays.HasValue && model.DeleteAfterDays.HasValue &&
                model.LocalRetentionDays.Value < model.DeleteAfterDays.Value)
            {
                ModelState.AddModelError("LocalRetentionDays",
                    _localizer["LocalRetentionMustBeGreaterOrEqual"].Value);
                return View("Create", model);
            }

            try
            {
                _logger.LogInformation("Importing all M365 tenant mailboxes for tenant {TenantId}", model.TenantId);

                var tenantUsers = await _graphEmailService.GetTenantMailboxUsersAsync(
                    model.ClientId,
                    model.ClientSecret,
                    model.TenantId,
                    includeDisabled: !model.SkipDisabledMailboxes);

                var tenantMailboxes = tenantUsers
                    .Select(user => new
                    {
                        Name = string.IsNullOrWhiteSpace(user.DisplayName)
                            ? user.UserPrincipalName ?? user.Mail ?? model.Name ?? _localizer["M365MailboxDefaultName"].Value
                            : user.DisplayName,
                        EmailAddress = string.IsNullOrWhiteSpace(user.Mail)
                            ? user.UserPrincipalName
                            : user.Mail
                    })
                    .Where(user => !string.IsNullOrWhiteSpace(user.EmailAddress))
                    .GroupBy(user => user.EmailAddress!.Trim().ToLowerInvariant())
                    .Select(group => group.First())
                    .ToList();

                if (tenantMailboxes.Count == 0)
                {
                    ModelState.AddModelError("", _localizer["NoEnabledM365UsersFound"].Value);
                    return View("Create", model);
                }

                var mailboxAddresses = tenantMailboxes
                    .Select(mailbox => mailbox.EmailAddress!.Trim().ToLowerInvariant())
                    .ToList();

                var existingAddresses = await ManageableMailAccounts()
                    .Where(account => account.Provider == ProviderType.M365 && mailboxAddresses.Contains(account.EmailAddress.ToLower()))
                    .Select(account => account.EmailAddress.ToLower())
                    .ToListAsync();

                var selectedAddressSet = model.ImportAllTenantMailboxes
                    ? null
                    : model.SelectedM365Mailboxes
                        .Select(address => address.Trim().ToLowerInvariant())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var selectedMailboxCount = selectedAddressSet?.Count ?? tenantMailboxes.Count;
                var accountNamePrefix = model.Name!.Trim();
                var existingAddressSet = existingAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var accountsToCreate = tenantMailboxes
                    .Where(mailbox => selectedAddressSet == null || selectedAddressSet.Contains(mailbox.EmailAddress!.Trim().ToLowerInvariant()))
                    .Where(mailbox => !existingAddressSet.Contains(mailbox.EmailAddress!))
                    .Select(mailbox => new MailAccount
                    {
                        Name = $"{accountNamePrefix} - <{mailbox.EmailAddress}>",
                        EmailAddress = mailbox.EmailAddress!,
                        UseSSL = model.UseSSL,
                        IsEnabled = model.IsEnabled,
                        Provider = ProviderType.M365,
                        ClientId = model.ClientId,
                        ClientSecret = model.ClientSecret,
                        TenantId = model.TenantId,
                        ExcludedFolders = string.Empty,
                        DeleteAfterDays = model.DeleteAfterDays,
                        LocalRetentionDays = model.LocalRetentionDays,
                        SyncIntervalMinutes = model.SyncIntervalMinutes,
                        FullSyncIntervalHours = model.FullSyncIntervalHours,
                        LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    })
                    .ToList();

                if (accountsToCreate.Count == 0)
                {
                    TempData["SuccessMessage"] = _localizer["AllSelectedM365TenantMailboxesAlreadyExist", selectedMailboxCount].Value;
                    return RedirectToAction(nameof(Index));
                }

                _context.MailAccounts.AddRange(accountsToCreate);
                await _context.SaveChangesAsync();

                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                var currentUser = await _context.Users
                    .FirstOrDefaultAsync(user => user.Username.ToLower() == currentUsername.ToLower());

                if (currentUser != null)
                {
                    _context.UserMailAccounts.AddRange(accountsToCreate.Select(account => new UserMailAccount
                    {
                        UserId = currentUser.Id,
                        MailAccountId = account.Id
                    }));
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Assigned {Count} M365 tenant accounts to owner {Username}",
                        accountsToCreate.Count, currentUser.Username);
                }

                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: $"Imported {accountsToCreate.Count} M365 tenant mail accounts for tenant {model.TenantId}");
                }

                TempData["SuccessMessage"] = _localizer["ImportedM365TenantAccounts", accountsToCreate.Count, selectedMailboxCount - accountsToCreate.Count].Value;
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing M365 tenant accounts: {Message}", ex.Message);
                ModelState.AddModelError("", _localizer["M365TenantAccountsCouldNotBeImported"].Value);
                return View("Create", model);
            }
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListM365TenantMailboxes(
            [FromForm] string clientId,
            [FromForm] string clientSecret,
            [FromForm] string tenantId,
            [FromForm] bool skipDisabledMailboxes = true)
        {
            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(tenantId))
            {
                return Json(new
                {
                    success = false,
                    message = _localizer["M365TenantCredentialsRequired"].Value
                });
            }

            try
            {
                var tenantUsers = await _graphEmailService.GetTenantMailboxUsersAsync(
                    clientId,
                    clientSecret,
                    tenantId,
                    includeDisabled: !skipDisabledMailboxes);

                var tenantMailboxes = tenantUsers
                    .Select(user => new
                    {
                        DisplayName = string.IsNullOrWhiteSpace(user.DisplayName)
                            ? user.UserPrincipalName ?? user.Mail ?? _localizer["M365MailboxDefaultName"].Value
                            : user.DisplayName,
                        EmailAddress = string.IsNullOrWhiteSpace(user.Mail)
                            ? user.UserPrincipalName
                            : user.Mail,
                        IsDisabled = user.AccountEnabled == false
                    })
                    .Where(user => !string.IsNullOrWhiteSpace(user.EmailAddress))
                    .GroupBy(user => user.EmailAddress!.Trim().ToLowerInvariant())
                    .Select(group => group.First())
                    .OrderBy(user => user.EmailAddress)
                    .ToList();

                var mailboxAddresses = tenantMailboxes
                    .Select(mailbox => mailbox.EmailAddress!.Trim().ToLowerInvariant())
                    .ToList();

                var existingAddresses = await ManageableMailAccounts()
                    .Where(account => account.Provider == ProviderType.M365 && mailboxAddresses.Contains(account.EmailAddress.ToLower()))
                    .Select(account => account.EmailAddress.ToLower())
                    .ToListAsync();
                var existingAddressSet = existingAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);

                return Json(new
                {
                    success = true,
                    mailboxes = tenantMailboxes.Select(mailbox => new
                    {
                        displayName = mailbox.DisplayName,
                        emailAddress = mailbox.EmailAddress,
                        isDisabled = mailbox.IsDisabled,
                        alreadyExists = existingAddressSet.Contains(mailbox.EmailAddress!)
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing M365 tenant mailboxes: {Message}", ex.Message);
                return Json(new
                {
                    success = false,
                    message = _localizer["M365TenantMailboxesCouldNotBeListed"].Value
                });
            }
        }

        // GET: MailAccounts/Edit/5
        public async Task<IActionResult> Edit(int id)
        {
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress,
                GroupName = account.GroupName,
                ImapServer = account.ImapServer,
                ImapPort = account.ImapPort,
                Username = account.Username,
                UseSSL = account.UseSSL,
                IsEnabled = account.IsEnabled,
                LastSync = account.LastSync,
                ExcludedFolders = account.ExcludedFolders,
                DeleteAfterDays = account.DeleteAfterDays,
                LocalRetentionDays = account.LocalRetentionDays,
                SyncIntervalMinutes = account.SyncIntervalMinutes,
                FullSyncIntervalHours = account.FullSyncIntervalHours,
                Provider = account.Provider,
                MailProviderKind = account.MailProviderKind,
                ProviderLabel = account.MailProviderKind is null
                    ? account.Provider.ToString()
                    : _mailProviderRegistry.For(account.MailProviderKind.Value).DisplayName,
                ClientId = account.Provider == ProviderType.M365 ? account.ClientId : null,
                ClientSecret = account.Provider == ProviderType.M365 ? account.ClientSecret : null,
                ExternalOAuthClientId = account.Provider == ProviderType.IMAP ? account.ClientId : null,
                ExternalOAuthClientSecret = null,
                ExternalOAuthRedirectUri = account.Provider == ProviderType.IMAP ? account.OAuthRedirectUri : null,
                TenantId = account.TenantId,
                MsaClientId = account.Provider == ProviderType.MSA ? account.ClientId : null,
                MsaIsAuthorized = account.Provider == ProviderType.MSA && !string.IsNullOrEmpty(account.OAuthRefreshToken),
                MsaTokenExpiry = account.OAuthTokenExpiry,
            };

            // Set ViewBag properties
            ViewBag.Provider = account.Provider;
            ViewBag.MsaHasDefaultClientId = _msaOptions.HasDefaultClientId;
            
            // Note: Folders are now loaded on-demand via AJAX to improve page load performance
            // The GetFolders endpoint handles folder loading when the user clicks the "Load Folders" button

            return View(model);
        }

        // POST: MailAccounts/ToggleEnabled/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleEnabled(int id)
        {
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Store the current status before toggling for logging
            bool wasEnabled = account.IsEnabled;

            // Toggle the enabled status
            account.IsEnabled = !account.IsEnabled;
            await _context.SaveChangesAsync();

            // Log the account enable/disable action
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
            if (!string.IsNullOrEmpty(currentUsername))
            {
                await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                    searchParameters: $"{(account.IsEnabled ? "Enabled" : "Disabled")} mail account: {account.Name}",
                    mailAccountId: account.Id);
            }

            // Correct message based on the NEW status (after toggling)
            TempData["SuccessMessage"] = account.IsEnabled
                ? _localizer["EmailAccountEnabled", account.Name].Value
                : _localizer["EmailAccountDisabled", account.Name].Value;

            return RedirectToAction(nameof(Index));
        }

        // POST: MailAccounts/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, MailAccountViewModel model, bool ApplyToAllDomainAccounts = false)
        {
            if (id != model.Id)
            {
                return NotFound();
            }

            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var persistedAccount = await _context.MailAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(account => account.Id == id);
            if (persistedAccount == null)
            {
                return NotFound();
            }

            IMailProviderModule? mailProviderModule = persistedAccount.MailProviderKind is not null
                ? _mailProviderRegistry.For(persistedAccount.MailProviderKind.Value)
                : null;

            if (persistedAccount.MailProviderKind is not null)
            {
                model.MailProviderKind = persistedAccount.MailProviderKind;
                model.ProviderLabel = _mailProviderRegistry
                    .For(persistedAccount.MailProviderKind.Value)
                    .DisplayName;

                if (model.Provider != persistedAccount.Provider)
                {
                    ModelState.AddModelError(nameof(model.Provider), "邮箱服务商创建后不能更改，请新建邮箱账号。");
                }

                if (!string.Equals(model.EmailAddress?.Trim(), persistedAccount.EmailAddress,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(nameof(model.EmailAddress), "邮箱地址创建后不能更改，请新建邮箱账号。");
                }

                // Never trust posted identity fields. They select the provider module and
                // own the account's authorization and archived-mail boundary.
                model.Provider = persistedAccount.Provider;
                model.EmailAddress = persistedAccount.EmailAddress;
            }

            if (model.Provider == ProviderType.IMAP)
            {
                if (!SupportedMailProviderPolicy.TryResolve(model.EmailAddress, out var preset))
                {
                    ModelState.AddModelError(nameof(model.EmailAddress), "IMAP 收件目前仅支持 Gmail、Yahoo 和 GMX 邮箱。");
                }
                else
                {
                    mailProviderModule ??= _mailProviderRegistry.Detect(model.EmailAddress);
                    model.ImapServer = preset.ImapServer;
                    model.ImapPort = preset.ImapPort;
                    model.UseSSL = preset.UseSsl;
                    model.Username = model.EmailAddress.Trim();
                    model.Name = MailAccountNamePolicy.Derive(model.EmailAddress);
                    ModelState.Remove(nameof(model.Name));
                }
            }
            else if (model.Provider == ProviderType.MSA)
            {
                model.ImapServer = "outlook.office365.com";
                model.ImapPort = 993;
                model.UseSSL = true;
                model.Name = MailAccountNamePolicy.Derive(model.EmailAddress);
                ModelState.Remove(nameof(model.Name));
            }
            else
            {
                ModelState.AddModelError(nameof(model.Provider), "本地版目前仅支持 Gmail、Yahoo、GMX 和 Outlook 个人邮箱。");
            }
            model.DeleteAfterDays = null;
            model.LocalRetentionDays = null;
            model.GroupName = string.Empty;

            // Remove password validation if left blank
            if (string.IsNullOrEmpty(model.Password))
            {
                ModelState.Remove("Password");
            }
            else if (model.Provider == ProviderType.IMAP && mailProviderModule is not null)
            {
                try
                {
                    model.Password = mailProviderModule.NormalizeAppPassword(model.Password);
                }
                catch (InvalidOperationException ex)
                {
                    ModelState.AddModelError(nameof(model.Password), ex.Message);
                }
            }

            ViewBag.MsaHasDefaultClientId = _msaOptions.HasDefaultClientId;

            // For MSA without a configured default ClientId, a per-account ClientId is required.
            if (model.Provider == ProviderType.MSA && !_msaOptions.HasDefaultClientId
                && string.IsNullOrWhiteSpace(model.MsaClientId)
                && string.IsNullOrWhiteSpace(model.ClientId))
            {
                ModelState.AddModelError("MsaClientId",
                    _localizer["MsaClientIdRequired"].Value);
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var account = await _context.MailAccounts.FindAsync(id);
                    if (account == null)
                    {
                        return NotFound();
                    }

                    account.Name = model.Provider == ProviderType.IMAP
                        ? MailAccountNamePolicy.Derive(model.EmailAddress)
                        : model.Name;
                    account.EmailAddress = model.EmailAddress;
                    account.GroupName = string.Empty;
                    account.ImapServer = model.ImapServer;
                    account.ImapPort = model.ImapPort;
                    // For MSA the Username holds the authorized identity captured from the
                    // OAuth id_token. The disabled form field is intentionally not posted,
                    // so ordinary edits must never overwrite that identity.
                    if (model.Provider == ProviderType.IMAP)
                    {
                        account.Username = model.EmailAddress.Trim();
                    }
                    account.IsEnabled = model.IsEnabled;
                    account.Provider = model.Provider;

                    if (model.Provider == ProviderType.M365)
                    {
                        account.ClientId = model.ClientId;
                        account.TenantId = model.TenantId;
                        if (!string.IsNullOrEmpty(model.ClientSecret))
                            account.ClientSecret = model.ClientSecret;
                        // Switching away from MSA: invalidate cached MSA OAuth tokens
                        account.OAuthAccessToken = null;
                        account.OAuthRefreshToken = null;
                        account.OAuthGrantedScopes = null;
                        account.OAuthTokenExpiry = null;
                        account.OAuthRedirectUri = null;
                    }
                    else if (model.Provider == ProviderType.MSA)
                    {
                        // Outlook authenticates exclusively through OAuth.
                        account.Password = null;
                        var clientSecretChanged = !string.IsNullOrEmpty(model.MsaClientSecret);

                        if (!string.IsNullOrEmpty(model.MsaClientId))
                        {
                            // User entered an override ClientId. Clear tokens only if it changed.
                            var clientIdChanged = model.MsaClientId != account.ClientId;
                            account.ClientId = model.MsaClientId;
                            if (clientIdChanged)
                            {
                                // New app registration — all tokens are invalid
                                account.OAuthAccessToken = null;
                                account.OAuthTokenExpiry = null;
                                account.OAuthRefreshToken = null;
                                account.OAuthGrantedScopes = null;
                            }
                            else if (clientSecretChanged)
                            {
                                // New secret — only cached access token needs refresh
                                account.OAuthAccessToken = null;
                                account.OAuthTokenExpiry = null;
                            }
                        }
                        else if (_msaOptions.HasDefaultClientId)
                        {
                            // No override entered → fall back to the shared default ClientId.
                            // Clearing a previous override is itself a credential change.
                            var clientIdChanged = account.ClientId != null;
                            account.ClientId = null;
                            if (clientIdChanged)
                            {
                                account.OAuthAccessToken = null;
                                account.OAuthTokenExpiry = null;
                                account.OAuthRefreshToken = null;
                                account.OAuthGrantedScopes = null;
                            }
                        }
                        // else: no override entered and no default configured → keep existing account.ClientId.

                        if (clientSecretChanged)
                            account.ClientSecret = model.MsaClientSecret;
                        account.TenantId = null;
                        account.OAuthRedirectUri = null;
                    }
                    else if (model.Provider == ProviderType.IMAP)
                    {
                        var clientIdChanged = !string.IsNullOrWhiteSpace(model.ExternalOAuthClientId) &&
                            !string.Equals(model.ExternalOAuthClientId.Trim(), account.ClientId, StringComparison.Ordinal);
                        var refreshTokenChanged = !string.IsNullOrWhiteSpace(model.OAuthRefreshToken);
                        if (!string.IsNullOrWhiteSpace(model.ExternalOAuthClientId))
                            account.ClientId = model.ExternalOAuthClientId.Trim();
                        if (!string.IsNullOrWhiteSpace(model.ExternalOAuthClientSecret))
                            account.ClientSecret = model.ExternalOAuthClientSecret;
                        if (refreshTokenChanged)
                            account.OAuthRefreshToken = model.OAuthRefreshToken!.Trim();
                        else if (clientIdChanged)
                        {
                            account.OAuthRefreshToken = null;
                            account.OAuthGrantedScopes = null;
                        }
                        if (!string.IsNullOrWhiteSpace(model.ExternalOAuthRedirectUri))
                            account.OAuthRedirectUri = model.ExternalOAuthRedirectUri.Trim();
                        if (clientIdChanged || refreshTokenChanged || !string.IsNullOrWhiteSpace(model.ExternalOAuthClientSecret) ||
                            !string.IsNullOrWhiteSpace(model.ExternalOAuthRedirectUri))
                        {
                            account.OAuthAccessToken = null;
                            account.OAuthTokenExpiry = null;
                        }
                        account.TenantId = null;
                    }
                    else
                    {
                        account.ClientId = null;
                        account.ClientSecret = null;
                        account.TenantId = null;
                        // Switching away from MSA: invalidate cached MSA OAuth tokens
                        account.OAuthAccessToken = null;
                        account.OAuthRefreshToken = null;
                        account.OAuthGrantedScopes = null;
                        account.OAuthTokenExpiry = null;
                        account.OAuthRedirectUri = null;
                    }

                    // Only update password if provided
                    if (model.Provider == ProviderType.IMAP && !string.IsNullOrEmpty(model.Password))
                    {
                        account.Password = _credentialEncryptionService.Encrypt(model.Password);
                    }

                    account.UseSSL = model.UseSSL;
                    account.ExcludedFolders = model.ExcludedFolders ?? string.Empty;
                    account.DeleteAfterDays = null;
                    account.LocalRetentionDays = null;
                    account.SyncIntervalMinutes = model.SyncIntervalMinutes;
                    account.FullSyncIntervalHours = model.FullSyncIntervalHours;

                    if (account.MailProviderKind is not null)
                    {
                        _mailProviderRegistry.For(account.MailProviderKind.Value).PrepareAccount(account);
                    }

                    // Validate local retention policy
                    if (account.LocalRetentionDays.HasValue && !account.DeleteAfterDays.HasValue)
                    {
                        ModelState.AddModelError("LocalRetentionDays", 
                            _localizer["LocalRetentionRequiresServerRetention"].Value);
                        return View(model);
                    }
                    
                    if (account.LocalRetentionDays.HasValue && account.DeleteAfterDays.HasValue &&
                        account.LocalRetentionDays.Value < account.DeleteAfterDays.Value)
                    {
                        ModelState.AddModelError("LocalRetentionDays", 
                            _localizer["LocalRetentionMustBeGreaterOrEqual"].Value);
                        return View(model);
                    }

                    // Test connection before saving (only for IMAP accounts)
                    if (!string.IsNullOrEmpty(model.Password) && account.Provider == ProviderType.IMAP)
                    {
                        var provider = await _providerFactory.GetServiceForAccountAsync(account.Id);

                        var connectionResult = await provider.TestConnectionAsync(account);
                        if (!connectionResult)
                        {
                            ModelState.AddModelError("", _localizer["EmailAccountError"]);
                            return View(model);
                        }
                    }

                    await _context.SaveChangesAsync();
                    if (account.IsEnabled && (account.Provider == ProviderType.IMAP ||
                        (account.Provider == ProviderType.MSA && !string.IsNullOrEmpty(account.OAuthRefreshToken))))
                    {
                        _onDemandSyncQueue.Enqueue(account.Id, MailSyncRequestPriority.Bulk, MailSyncRequestKind.ValidateConnection);
                    }
                    
                    // Log the account update action
                    var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                    var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                    if (!string.IsNullOrEmpty(currentUsername))
                    {
                        await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                            searchParameters: $"Updated mail account: {account.Name}",
                            mailAccountId: account.Id);
                    }
                    
                    // Handle bulk update if requested
                    if (ApplyToAllDomainAccounts && account.Provider == ProviderType.M365)
                    {
                        _logger.LogInformation("Bulk update requested for account {AccountId} ({AccountName})", account.Id, account.Name);
                        
                        var domain = ExtractDomain(account.EmailAddress);
                        if (!string.IsNullOrEmpty(domain))
                        {
                            // Get user info for permission checking
                            var isAdmin = authService.IsCurrentUserAdmin(HttpContext);
                            var isSelfManager = authService.IsCurrentUserSelfManager(HttpContext);

                            IQueryable<MailAccount> accountsQuery = _context.MailAccounts
                                .Where(ma => ma.Provider == ProviderType.M365 &&
                                             ma.EmailAddress.ToLower().Contains("@" + domain) &&
                                             ma.Id != account.Id);

                            accountsQuery = accountsQuery
                                .Where(ma => ma.UserMailAccounts.Any(uma => uma.User.Username.ToLower() == currentUsername.ToLower()));

                            var accountsToUpdate = await accountsQuery.ToListAsync();
                            
                            if (accountsToUpdate.Any())
                            {
                                _logger.LogInformation("Updating {Count} accounts in domain {Domain}", accountsToUpdate.Count, domain);
                                
                                foreach (var acc in accountsToUpdate)
                                {
                                    acc.ClientId = account.ClientId;
                                    if (!string.IsNullOrEmpty(account.ClientSecret))
                                    {
                                        acc.ClientSecret = account.ClientSecret;
                                    }
                                    acc.TenantId = account.TenantId;
                                }
                                
                                await _context.SaveChangesAsync();
                                
                                // Log the bulk update
                                if (!string.IsNullOrEmpty(currentUsername))
                                {
                                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                                        searchParameters: $"Bulk updated M365 credentials for {accountsToUpdate.Count} accounts in domain {domain}");
                                }
                                
                                TempData["SuccessMessage"] = _localizer["EmailAccountUpdateSuccess"].Value + 
                                    $" Updated credentials for {accountsToUpdate.Count} additional account(s) in the same domain.";
                            }
                            else
                            {
                                TempData["SuccessMessage"] = _localizer["EmailAccountUpdateSuccess"].Value;
                            }
                        }
                        else
                        {
                            TempData["SuccessMessage"] = _localizer["EmailAccountUpdateSuccess"].Value;
                        }
                    }
                    else
                    {
                        TempData["SuccessMessage"] = _localizer["EmailAccountUpdateSuccess"].Value;
                    }
                    
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await _context.MailAccounts.AnyAsync(e => e.Id == id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            return View(model);
        }

        // GET: MailAccounts/Delete/5
        public async Task<IActionResult> Delete(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // E-Mail-Anzahl abrufen (use EmailCoreService to support all provider types including IMPORT)
            var emailCount = await _emailCoreService.GetEmailCountByAccountAsync(id);

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress
            };

            // ViewBag für die E-Mail-Anzahl setzen
            ViewBag.EmailCount = emailCount;

            return View(model);
        }

        // POST: MailAccounts/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Determine number of emails to delete
            var emailCount = await _context.ArchivedEmails.CountAsync(e => e.MailAccountId == id);

            _logger.LogInformation("Queueing deletion for account {AccountId} with {Count} email(s)", id, emailCount);

            // Get current user info for logging
            var authService = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);

            // Account deletion always runs in the background. Even a small mailbox can
            // contain large attachment payloads, and deleting it in the HTTP request
            // freezes the local app until SQLite finishes the work.
            _mailAccountDeletionService.QueueDeletion(id, account.Name, currentUsername ?? "System");
            TempData["SuccessMessage"] = _localizer["AccountDeletionQueued", account.Name].Value;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WhitelistDeletionPreview(WhitelistDeletionRequest request)
        {
            var whitelist = WhitelistDeletionParser.ExtractComAddresses(request.SourceText);
            if (whitelist.Count == 0)
            {
                return BadRequest(new
                {
                    message = "未识别到以 .com 结尾的完整邮箱地址。"
                });
            }

            var accounts = await ManageableMailAccounts()
                .AsNoTracking()
                .Select(account => new
                {
                    account.Id,
                    account.EmailAddress,
                    EmailCount = account.ArchivedEmails.Count()
                })
                .ToListAsync();

            var keptAccounts = accounts
                .Where(account => whitelist.Contains(account.EmailAddress))
                .OrderBy(account => account.EmailAddress)
                .ToList();

            if (keptAccounts.Count == 0)
            {
                return BadRequest(new
                {
                    message = $"识别到 {whitelist.Count:N0} 个 .com 邮箱，但没有匹配任何可管理账号。为防止全部误删，操作已中止。"
                });
            }

            var deletedAccounts = accounts
                .Where(account => !whitelist.Contains(account.EmailAddress))
                .OrderBy(account => account.EmailAddress)
                .ToList();

            return Json(new
            {
                recognizedCount = whitelist.Count,
                keepCount = keptAccounts.Count,
                unmatchedCount = whitelist.Count - keptAccounts.Count,
                deleteAccountCount = deletedAccounts.Count,
                deleteEmailCount = deletedAccounts.Sum(account => account.EmailCount),
                keepEmails = keptAccounts.Take(8).Select(account => account.EmailAddress),
                deleteEmails = deletedAccounts.Take(8).Select(account => account.EmailAddress),
                previewFingerprint = BuildDeletionFingerprint(deletedAccounts.Select(account => account.Id))
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WhitelistDelete(WhitelistDeletionRequest request)
        {
            var whitelist = WhitelistDeletionParser.ExtractComAddresses(request.SourceText);
            if (whitelist.Count == 0)
            {
                TempData["ErrorMessage"] = "未识别到以 .com 结尾的完整邮箱地址，未删除任何账号。";
                return RedirectToAction(nameof(Index));
            }

            var accounts = await ManageableMailAccounts()
                .AsNoTracking()
                .Select(account => new
                {
                    account.Id,
                    account.Name,
                    account.EmailAddress
                })
                .ToListAsync();

            if (!accounts.Any(account => whitelist.Contains(account.EmailAddress)))
            {
                TempData["ErrorMessage"] = "白名单没有匹配任何可管理账号，为防止全部误删，未删除任何账号。";
                return RedirectToAction(nameof(Index));
            }

            var accountsToDelete = accounts
                .Where(account => !whitelist.Contains(account.EmailAddress))
                .OrderBy(account => account.Id)
                .ToList();

            var currentFingerprint = BuildDeletionFingerprint(accountsToDelete.Select(account => account.Id));
            if (string.IsNullOrWhiteSpace(request.PreviewFingerprint) ||
                !string.Equals(currentFingerprint, request.PreviewFingerprint, StringComparison.Ordinal))
            {
                TempData["ErrorMessage"] = "账号列表在预览后发生了变化，请重新执行加白删除。";
                return RedirectToAction(nameof(Index));
            }

            var authService = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext) ?? "System";
            foreach (var account in accountsToDelete)
            {
                _mailAccountDeletionService.QueueDeletion(account.Id, account.Name, currentUsername);
            }

            TempData["SuccessMessage"] = accountsToDelete.Count == 0
                ? "所有可管理邮箱都在白名单中，没有账号需要删除。"
                : $"已提交删除 {accountsToDelete.Count:N0} 个非白名单邮箱，任务将在后台执行。";

            return RedirectToAction(nameof(Index));
        }

        private static string BuildDeletionFingerprint(IEnumerable<int> accountIds)
        {
            var source = string.Join(",", accountIds.Order());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        }

        // GET: MailAccounts/DeletionStatus
        [HttpGet]
        public async Task<IActionResult> DeletionStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidDeletionJobID"].Value;
                return RedirectToAction(nameof(Index));
            }

            var job = _mailAccountDeletionService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = _localizer["DeletionJobNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Check if user had access to the account (if account still exists)
            if (!job.IsCompleted)
            {
                if (!await HasAccessToAccountAsync(job.MailAccountId))
                {
                    return NotFound();
                }
            }

            return View(job);
        }

        // POST: MailAccounts/CancelDeletion
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelDeletion(string jobId, string returnUrl = null)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidDeletionJobID"].Value;
                return Redirect(returnUrl ?? Url.Action(nameof(Index)));
            }

            var success = _mailAccountDeletionService.CancelJob(jobId);
            if (success)
            {
                TempData["SuccessMessage"] = _localizer["DeletionCancelled"].Value;
            }
            else
            {
                TempData["ErrorMessage"] = _localizer["DeletionCancelError"].Value;
            }

            return Redirect(returnUrl ?? Url.Action(nameof(Index)));
        }

        // POST: MailAccounts/Sync/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Sync(int id)
        {
            var isMailboxRefresh = string.Equals(
                Request.Headers["X-Requested-With"],
                "XMLHttpRequest",
                StringComparison.OrdinalIgnoreCase);
            var requestedAt = DateTime.UtcNow;

            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Prevent sync for import-only accounts
            if (account.Provider == ProviderType.IMPORT)
            {
                if (isMailboxRefresh)
                    return BadRequest(new { message = _localizer["ImportOnlyAccountNoSync"].Value });
                TempData["ErrorMessage"] = _localizer["ImportOnlyAccountNoSync"].Value;
                return RedirectToAction("Index", "Emails", new { SelectedAccountId = id });
            }

            if (!account.IsEnabled)
            {
                const string message = "该邮箱已停用，请先启用后再同步。";
                if (isMailboxRefresh)
                    return BadRequest(new { message });
                TempData["ErrorMessage"] = message;
                return RedirectToAction(nameof(Index));
            }

            // MSA accounts require OAuth authorization before they can sync.
            // Block the sync early with a helpful redirect instead of letting it fail
            // deep in the IMAP connection factory with a cryptic exception.
            if (account.Provider == ProviderType.MSA && string.IsNullOrEmpty(account.OAuthRefreshToken))
            {
                if (isMailboxRefresh)
                    return BadRequest(new { message = _localizer["MsaNotAuthorizedSync"].Value });
                TempData["ErrorMessage"] = _localizer["MsaNotAuthorizedSync"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            var queueStatus = _onDemandSyncQueue.Enqueue(id, MailSyncRequestPriority.Interactive);
            if (isMailboxRefresh)
            {
                return Json(new
                {
                    state = queueStatus.State.ToString(),
                    requestedAt = queueStatus.State == MailSyncQueueState.Running
                        ? null
                        : requestedAt.ToString("O")
                });
            }

            TempData["SuccessMessage"] = queueStatus.State == MailSyncQueueState.Running
                ? "该邮箱正在同步，页面会自动更新。"
                : "已优先加入同步队列，页面会自动更新。";

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WatchSync(int id)
        {
            if (!await HasAccessToAccountAsync(id))
                return NotFound();

            var account = await _context.MailAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.IsEnabled && a.Provider != ProviderType.IMPORT);
            if (account is null)
                return NotFound();

            var status = _onDemandSyncQueue.GetStatus(id);
            return Json(new
            {
                state = status.State.ToString(),
                kind = status.Kind?.ToString()
            });
        }

        [HttpGet]
        public async Task<IActionResult> SyncStatus(int id, DateTime? startedAfter = null)
        {
            if (!await HasAccessToAccountAsync(id))
                return NotFound();

            var status = _onDemandSyncQueue.GetStatus(id);
            var latestJob = _syncJobService.GetAllJobs()
                .Where(job => job.MailAccountId == id &&
                    (!startedAfter.HasValue || job.Started >= startedAfter.Value.ToUniversalTime()))
                .OrderByDescending(job => job.Started)
                .FirstOrDefault();

            return Json(new
            {
                state = status.State.ToString(),
                kind = status.Kind?.ToString(),
                job = latestJob is null ? null : new
                {
                    status = latestJob.Status.ToString(),
                    latestJob.NewEmails,
                    latestJob.ProcessedEmails,
                    latestJob.FailedEmails,
                    latestJob.ErrorMessage,
                    completed = latestJob.Completed?.ToString("O")
                }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncAll()
        {
            var authService = HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();
            var userId = authService.GetCurrentUserId(HttpContext);
            if (!userId.HasValue)
                return Forbid();

            var accountIds = await _context.MailAccounts
                .AsNoTracking()
                .Where(a =>
                    a.IsEnabled &&
                    a.Provider != ProviderType.IMPORT &&
                    a.UserMailAccounts.Any(ownership => ownership.UserId == userId.Value))
                .Select(a => a.Id)
                .ToListAsync();

            foreach (var accountId in accountIds)
                _onDemandSyncQueue.Enqueue(accountId, MailSyncRequestPriority.Bulk);

            TempData["SuccessMessage"] = $"已将 {accountIds.Count} 个邮箱加入同步全部队列。正在查看的邮箱会优先执行。";
            return RedirectToAction(nameof(Index));
        }

        // POST: MailAccounts/Resync/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resync(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Prevent resync for import-only accounts
            if (account.Provider == ProviderType.IMPORT)
            {
                TempData["ErrorMessage"] = _localizer["ImportOnlyAccountNoSync"].Value;
                return RedirectToAction(nameof(Details), new { id });
            }

            try
            {
                var provider = await _providerFactory.GetServiceForAccountAsync(id);
                var success = await provider.ResyncAccountAsync(id);
                if (success)
                {
                    // Log the resync action
                    var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                    var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                    if (!string.IsNullOrEmpty(currentUsername))
                    {
                        await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                            searchParameters: $"Started resync for mail account: {account.Name}",
                            mailAccountId: account.Id);
                    }
                    
                    TempData["SuccessMessage"] = _localizer["FullSyncStarted", account.Name].Value;
                }
                else
                {
                    TempData["ErrorMessage"] = _localizer["FullSyncFailed", account.Name].Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting resync for account {AccountName}: {Message}", account.Name, ex.Message);
                TempData["ErrorMessage"] = $"{_localizer["FullSyncError"]}: {ex.Message}";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        // POST: MailAccounts/MoveAllEmails/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAllEmails(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null) return NotFound();

            var emailIds = await _context.ArchivedEmails
                .Where(e => e.MailAccountId == id)
                .Select(e => e.Id)
                .ToListAsync();

            if (!emailIds.Any())
            {
                TempData["ErrorMessage"] = _localizer["MoveEmailsNotFound"].Value;
                return RedirectToAction(nameof(Details), new { id });
            }

            _logger.LogInformation("Account {AccountId} has {Count} emails. Thresholds: Async={AsyncThreshold}, MaxAsync={MaxAsync}",
                id, emailIds.Count, _batchOptions.AsyncThreshold, _batchOptions.MaxAsyncEmails);

            // Prüfe absolute Limits
            if (emailIds.Count > _batchOptions.MaxAsyncEmails)
            {
                TempData["ErrorMessage"] = _localizer["TooManyEmailsInAccount", emailIds.Count, _batchOptions.MaxAsyncEmails].Value;
                return RedirectToAction(nameof(Details), new { id });
            }

            // Entscheide basierend auf Schwellenwert
            if (emailIds.Count > _batchOptions.AsyncThreshold)
            {
                // Für große Mengen: Direkt zum asynchronen Batch-Restore
                _logger.LogInformation("Using background job for {Count} emails from account {AccountId}", emailIds.Count, id);
                return RedirectToAction("StartAsyncBatchRestoreFromAccount", "Emails", new
                {
                    accountId = id,
                    returnUrl = Url.Action("Details", new { id }),
                    preserveFolders = true
                });
            }
            else
            {
                // Für kleinere Mengen: Session-basierte Verarbeitung
                _logger.LogInformation("Using direct processing for {Count} emails from account {AccountId}", emailIds.Count, id);
                try
                {
                    HttpContext.Session.SetString("BatchRestoreIds", string.Join(",", emailIds));
                    HttpContext.Session.SetString("BatchRestoreReturnUrl", Url.Action("Details", new { id }));
                    HttpContext.Session.SetString("BatchRestorePreserveFolders", "true");
                    return RedirectToAction("BatchRestore", "Emails");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to store {Count} email IDs in session for account {AccountId}", emailIds.Count, id);
                    // Fallback zu Background Job
                    _logger.LogWarning("Session storage failed, redirecting to background job");
                    return RedirectToAction("StartAsyncBatchRestoreFromAccount", "Emails", new
                    {
                        accountId = id,
                        returnUrl = Url.Action("Details", new { id }),
                        preserveFolders = true
                    });
                }
            }
        }

        // GET: MailAccounts/ImportMBox
        public async Task<IActionResult> ImportMBox()
        {
            // Use the authentication service to get user info properly
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
            var isAdmin = authService.IsCurrentUserAdmin(HttpContext);
            var isSelfManager = authService.IsCurrentUserSelfManager(HttpContext);

            IQueryable<MailAccount> mailAccountsQuery;

            mailAccountsQuery = _context.MailAccounts
                .Where(ma => ma.UserMailAccounts.Any(uma => uma.User.Username.ToLower() == currentUsername.ToLower()));

            var accounts = await mailAccountsQuery
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            var model = new MBoxImportViewModel
            {
                AvailableAccounts = accounts.Select(a => new SelectListItem
                {
                    Value = a.Id.ToString(),
                    Text = $"{a.Name} ({a.EmailAddress})"
                }).ToList(),
                MaxFileSize = _uploadOptions.MaxFileSizeBytes,
                AccountProviders = accounts.ToDictionary(a => a.Id, a => a.Provider)
            };

            return View(model);
        }

        // POST: MailAccounts/ImportMBox
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(long.MaxValue)]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<IActionResult> ImportMBox(MBoxImportViewModel model)
        {
            // Reload accounts for validation failure
            var accounts = await _context.MailAccounts
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            model.AvailableAccounts = accounts.Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = $"{a.Name} ({a.EmailAddress})",
                Selected = a.Id == model.TargetAccountId
            }).ToList();

            // Ensure MaxFileSize is set for validation failures
            model.MaxFileSize = _uploadOptions.MaxFileSizeBytes;

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Validate file
            if (model.MBoxFile == null || model.MBoxFile.Length == 0)
            {
                ModelState.AddModelError("MBoxFile", _localizer["SelectValidMBoxFile"]);
                return View(model);
            }

            if (model.MBoxFile.Length > model.MaxFileSize)
            {
                ModelState.AddModelError("MBoxFile", _localizer["MBoxFileTooLarge", model.MaxFileSizeFormatted].Value);
                return View(model);
            }

            // Validate file extension
            if (!FileUploadHelper.IsAllowedImportExtension(model.MBoxFile.FileName))
            {
                ModelState.AddModelError("MBoxFile", _localizer["InvalidImportFileType"].Value);
                return View(model);
            }

            // Validate target account
            var targetAccount = await _context.MailAccounts.FindAsync(model.TargetAccountId);
            if (targetAccount == null)
            {
                ModelState.AddModelError("TargetAccountId", _localizer["SelectedAccountNotFound"]);
                return View(model);
            }

            try
            {
                // Save uploaded file
                var filePath = await _mboxImportService.SaveUploadedFileAsync(model.MBoxFile);

                // Create import job
                var job = new MBoxImportJob
                {
                    FileName = model.MBoxFile.FileName,
                    FilePath = filePath,
                    FileSize = model.MBoxFile.Length,
                    TargetAccountId = model.TargetAccountId,
                    TargetFolder = model.TargetFolder,
                    UserId = HttpContext.User.Identity?.Name ?? "Anonymous"
                };

                // Estimate email count
                job.TotalEmails = await _mboxImportService.EstimateEmailCountAsync(filePath);

                // Queue the job
                var jobId = _mboxImportService.QueueImport(job);

                // Log the MBox import action
                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                        searchParameters: $"Started MBox import for mail account: {targetAccount.Name}",
                        mailAccountId: targetAccount.Id);
                }

                TempData["SuccessMessage"] = _localizer["MBoxImportStarted", model.MBoxFile.FileName, job.TotalEmails].Value;
                return RedirectToAction("MBoxImportStatus", new { jobId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting MBox import for file {FileName}", model.MBoxFile.FileName);
                ModelState.AddModelError("", $"{_localizer["MBoxImportError"]}: {ex.Message}");
                return View(model);
            }
        }

        // GET: MailAccounts/MBoxImportStatus
        [HttpGet]
        public async Task<IActionResult> MBoxImportStatus(string jobId)
        {
            // Validate jobId parameter
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidMBoxID"].Value;
                return RedirectToAction(nameof(Index));
            }

            var job = _mboxImportService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = _localizer["MBoxImportJobNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Check if user has access to the target account
            if (!await HasAccessToAccountAsync(job.TargetAccountId))
            {
                return NotFound();
            }

            return View(job);
        }

        // POST: MailAccounts/CancelMBoxImport
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelMBoxImport(string jobId, string returnUrl = null)
        {
            // Validate jobId parameter
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidMBoxID"].Value;
                // Wenn returnUrl angegeben ist, leite dorthin weiter, sonst zur Index-Seite
                return Redirect(returnUrl ?? Url.Action(nameof(Index)));
            }

            var job = _mboxImportService.GetJob(jobId);
            if (job != null)
            {
                // Check if user has access to the target account
                if (!await HasAccessToAccountAsync(job.TargetAccountId))
                {
                    return NotFound();
                }
            }

            var success = _mboxImportService.CancelJob(jobId);
            if (success)
            {
                TempData["SuccessMessage"] = _localizer["MBoxImportCancelled"].Value;
            }
            else
            {
                TempData["ErrorMessage"] = _localizer["MBoxImportCancelError"].Value;
            }

            // Wenn returnUrl angegeben ist, leite dorthin weiter, sonst zur Index-Seite
            return Redirect(returnUrl ?? Url.Action(nameof(Index)));
        }

        // POST: MailAccounts/ResetSyncTime/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetSyncTime(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Set LastSync to current time
            account.LastSync = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = _localizer["SyncTimeResetSuccess"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        // GET: MailAccounts/EditSyncTime/5
        public async Task<IActionResult> EditSyncTime(int id)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            var model = new EditSyncTimeViewModel
            {
                Id = account.Id,
                AccountName = account.Name,
                CurrentSyncTime = account.LastSync,
                NewSyncTime = account.LastSync
            };

            return View(model);
        }

        // POST: MailAccounts/EditSyncTime/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSyncTime(int id, EditSyncTimeViewModel model)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                // Reload account name for display
                var account = await _context.MailAccounts.FindAsync(id);
                if (account != null)
                {
                    model.AccountName = account.Name;
                }
                return View(model);
            }

            var accountToUpdate = await _context.MailAccounts.FindAsync(id);
            if (accountToUpdate == null)
            {
                return NotFound();
            }

            // Set LastSync to the specified time (treat as local time and convert to UTC)
            accountToUpdate.LastSync = model.NewSyncTime.ToUniversalTime();
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = _localizer["SyncTimeUpdatedSuccess"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        // GET: MailAccounts/ImportEml
        public async Task<IActionResult> ImportEml()
        {
            // Use the authentication service to get user info properly
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
            var isAdmin = authService.IsCurrentUserAdmin(HttpContext);
            var isSelfManager = authService.IsCurrentUserSelfManager(HttpContext);

            IQueryable<MailAccount> mailAccountsQuery;

            mailAccountsQuery = _context.MailAccounts
                .Where(ma => ma.UserMailAccounts.Any(uma => uma.User.Username.ToLower() == currentUsername.ToLower()));

            var accounts = await mailAccountsQuery
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            var model = new EmlImportViewModel
            {
                AvailableAccounts = accounts.Select(a => new SelectListItem
                {
                    Value = a.Id.ToString(),
                    Text = $"{a.Name} ({a.EmailAddress})"
                }).ToList(),
                MaxFileSize = _uploadOptions.MaxFileSizeBytes
            };

            return View(model);
        }

        // POST: MailAccounts/ImportEml
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(long.MaxValue)]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<IActionResult> ImportEml(EmlImportViewModel model)
        {
            // Reload accounts for validation failure
            var accounts = await _context.MailAccounts
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            model.AvailableAccounts = accounts.Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = $"{a.Name} ({a.EmailAddress})",
                Selected = a.Id == model.TargetAccountId
            }).ToList();

            // Ensure MaxFileSize is set for validation failures
            model.MaxFileSize = _uploadOptions.MaxFileSizeBytes;

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Validate file
            if (model.EmlFile == null || model.EmlFile.Length == 0)
            {
                ModelState.AddModelError("EmlFile", _localizer["SelectValidEmlFile"]);
                return View(model);
            }

            if (model.EmlFile.Length > model.MaxFileSize)
            {
                ModelState.AddModelError("EmlFile", _localizer["EmlFileTooLarge", model.MaxFileSizeFormatted].Value);
                return View(model);
            }

            // Validate file extension
            if (!FileUploadHelper.IsAllowedImportExtension(model.EmlFile.FileName))
            {
                ModelState.AddModelError("EmlFile", _localizer["InvalidImportFileType"].Value);
                return View(model);
            }

            // Validate target account
            var targetAccount = await _context.MailAccounts.FindAsync(model.TargetAccountId);
            if (targetAccount == null)
            {
                ModelState.AddModelError("TargetAccountId", _localizer["SelectedAccountNotFound"]);
                return View(model);
            }

            try
            {
                // Save uploaded file
                var filePath = await _emlImportService.SaveUploadedFileAsync(model.EmlFile);

                // Create import job
                var job = new EmlImportJob
                {
                    FileName = model.EmlFile.FileName,
                    FilePath = filePath,
                    FileSize = model.EmlFile.Length,
                    TargetAccountId = model.TargetAccountId,
                    UserId = HttpContext.User.Identity?.Name ?? "Anonymous"
                };

                // Estimate email count
                job.TotalEmails = await _emlImportService.EstimateEmailCountAsync(filePath);

                // Queue the job
                var jobId = _emlImportService.QueueImport(job);

                // Log the EML import action
                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account, 
                        searchParameters: $"Started EML import for mail account: {targetAccount.Name}",
                        mailAccountId: targetAccount.Id);
                }

                TempData["SuccessMessage"] = _localizer["EmlImportStarted", model.EmlFile.FileName, job.TotalEmails].Value;
                return RedirectToAction("EmlImportStatus", new { jobId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting EML import for file {FileName}", model.EmlFile.FileName);
                ModelState.AddModelError("", $"{_localizer["EmlImportError"]}: {ex.Message}");
                return View(model);
            }
        }

        // GET: MailAccounts/EmlImportStatus
        [HttpGet]
        public async Task<IActionResult> EmlImportStatus(string jobId)
        {
            // Validate jobId parameter
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidEmlID"].Value;
                return RedirectToAction(nameof(Index));
            }

            var job = _emlImportService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = _localizer["EmlImportJobNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Check if user has access to the target account
            if (!await HasAccessToAccountAsync(job.TargetAccountId))
            {
                return NotFound();
            }

            return View(job);
        }

        // POST: MailAccounts/CancelEmlImport
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelEmlImport(string jobId, string returnUrl = null)
        {
            // Validate jobId parameter
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidEmlID"].Value;
                // Wenn returnUrl angegeben ist, leite dorthin weiter, sonst zur Index-Seite
                return Redirect(returnUrl ?? Url.Action(nameof(Index)));
            }

            var job = _emlImportService.GetJob(jobId);
            if (job != null)
            {
                // Check if user has access to the target account
                if (!await HasAccessToAccountAsync(job.TargetAccountId))
                {
                    return NotFound();
                }
            }

            var success = _emlImportService.CancelJob(jobId);
            if (success)
            {
                TempData["SuccessMessage"] = _localizer["EmlImportCancelled"].Value;
            }
            else
            {
                TempData["ErrorMessage"] = _localizer["EmlImportCancelError"].Value;
            }

            // Wenn returnUrl angegeben ist, leite dorthin weiter, sonst zur Index-Seite
            return Redirect(returnUrl ?? Url.Action(nameof(Index)));
        }

        // GET: MailAccounts/Export/5
        [SelfManagerRequired]
        public async Task<IActionResult> Export(int id)
        {
            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Get email counts
            var emailCount = await _emailCoreService.GetEmailCountByAccountAsync(id);
            var incomingCount = await _context.ArchivedEmails
                .CountAsync(e => e.MailAccountId == id && !e.IsOutgoing);
            var outgoingCount = await _context.ArchivedEmails
                .CountAsync(e => e.MailAccountId == id && e.IsOutgoing);

            if (emailCount == 0)
            {
                TempData["ErrorMessage"] = _localizer["NoEmailsToExport"].Value;
                return RedirectToAction(nameof(Details), new { id });
            }

            var model = new AccountExportViewModel
            {
                MailAccountId = id,
                MailAccountName = account.Name,
                TotalEmailsCount = emailCount,
                IncomingEmailsCount = incomingCount,
                OutgoingEmailsCount = outgoingCount
            };

            return View(model);
        }

        // POST: MailAccounts/Export
        [HttpPost]
        [ValidateAntiForgeryToken]
        [SelfManagerRequired]
        public async Task<IActionResult> Export(AccountExportViewModel model)
        {
            if (!await HasAccessToAccountAsync(model.MailAccountId))
            {
                return NotFound();
            }

            var account = await _context.MailAccounts.FindAsync(model.MailAccountId);
            if (account == null)
            {
                return NotFound();
            }

            // Validate email count
            var emailCount = await _emailCoreService.GetEmailCountByAccountAsync(model.MailAccountId);
            if (emailCount == 0)
            {
                TempData["ErrorMessage"] = _localizer["NoEmailsToExport"].Value;
                return RedirectToAction(nameof(Details), new { id = model.MailAccountId });
            }

            try
            {
                // Get current user info
                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);

                    // Log the export action
                    if (!string.IsNullOrEmpty(currentUsername))
                    {
                        await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Download, 
                            searchParameters: $"Started export for mail account: {account.Name} in {model.Format} format",
                            mailAccountId: model.MailAccountId);
                    }

                // Queue the job
                var jobId = _exportService.QueueExport(model.MailAccountId, model.Format, currentUsername ?? "Anonymous");

                TempData["SuccessMessage"] = _localizer["ExportStarted", account.Name, model.Format].Value;
                return RedirectToAction("ExportStatus", new { jobId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting export for account {AccountName}", account.Name);
                TempData["ErrorMessage"] = $"{_localizer["ExportError"]}: {ex.Message}";
                return RedirectToAction(nameof(Details), new { id = model.MailAccountId });
            }
        }

        // GET: MailAccounts/ExportStatus
        [HttpGet]
        [SelfManagerRequired]
        public async Task<IActionResult> ExportStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidExportID"].Value;
                return RedirectToAction(nameof(Index));
            }

            var job = _exportService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = _localizer["ExportJobNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Check if user has access to the account
            if (!await HasAccessToAccountAsync(job.MailAccountId))
            {
                return NotFound();
            }

            return View(job);
        }

        // GET: MailAccounts/DownloadExport
        [HttpGet]
        [SelfManagerRequired]
        public async Task<IActionResult> DownloadExport(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidExportID"].Value;
                return RedirectToAction(nameof(Index));
            }

            var job = _exportService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = _localizer["ExportJobNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Check if user has access to the account
            if (!await HasAccessToAccountAsync(job.MailAccountId))
            {
                return NotFound();
            }

            if (job.Status != AccountExportJobStatus.Completed)
            {
                TempData["ErrorMessage"] = _localizer["ExportFileNotFound"].Value;
                return RedirectToAction("ExportStatus", new { jobId });
            }

            try
            {
                var fileResult = _exportService.GetExportForDownload(jobId);
                if (fileResult == null || string.IsNullOrEmpty(fileResult.FilePath) || !System.IO.File.Exists(fileResult.FilePath))
                {
                    TempData["ErrorMessage"] = _localizer["ExportFileNotFound"].Value;
                    return RedirectToAction("ExportStatus", new { jobId });
                }
                
                // Stream the file directly without loading it into memory
                var fileStream = new FileStream(fileResult.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                
                // Mark as downloaded - the file will be deleted after download completes
                _exportService.MarkAsDownloaded(jobId);

                return File(fileStream, fileResult.ContentType, fileResult.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading export {JobId}", jobId);
                TempData["ErrorMessage"] = _localizer["ExportDownloadError"].Value;
                return RedirectToAction("ExportStatus", new { jobId });
            }
        }

        // POST: MailAccounts/CancelExport
        [HttpPost]
        [ValidateAntiForgeryToken]
        [SelfManagerRequired]
        public async Task<IActionResult> CancelExport(string jobId, string returnUrl = null)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                TempData["ErrorMessage"] = _localizer["InvalidExportID"].Value;
                return Redirect(returnUrl ?? Url.Action(nameof(Index)));
            }

            var job = _exportService.GetJob(jobId);
            if (job != null)
            {
                // Check if user has access to the account
                if (!await HasAccessToAccountAsync(job.MailAccountId))
                {
                    return NotFound();
                }
            }

            var success = _exportService.CancelJob(jobId);
            if (success)
            {
                TempData["SuccessMessage"] = _localizer["ExportCancelled"].Value;
            }
            else
            {
                TempData["ErrorMessage"] = _localizer["ExportCancelError"].Value;
            }

            return Redirect(returnUrl ?? Url.Action(nameof(Index)));
        }

        // AJAX endpoint for folder loading
        [HttpGet]
        public async Task<JsonResult> GetFolders(int accountId)
        {
            // Use proper authentication service
            if (!await HasAccessToAccountAsync(accountId))
            {
                return Json(new List<string> { "INBOX" });
            }

            try
            {
                var account = await _context.MailAccounts.FindAsync(accountId);
                if (account == null)
                {
                    return Json(new List<string> { "INBOX" });
                }

                if (account.Provider == ProviderType.IMPORT)
                {
                    // Für IMPORT-Konten: Existierende Ordner aus der Datenbank abrufen
                    var folders = await _context.ArchivedEmails
                        .Where(e => e.MailAccountId == accountId && !string.IsNullOrEmpty(e.FolderName))
                        .Select(e => e.FolderName)
                        .Distinct()
                        .OrderBy(f => f)
                        .ToListAsync();
                    
                    // IMMER "INBOX" als Standardoption hinzufügen
                    if (!folders.Contains("INBOX"))
                    {
                        folders.Insert(0, "INBOX");
                    }
                    
                    return Json(folders);
                }
                else if (account.Provider == ProviderType.M365)
                {
                    // Für M365-Konten den GraphEmailService verwenden
                    var folders = await _graphEmailService.GetMailFoldersAsync(account);
                    if (!folders.Any())
                    {
                        return Json(new List<string> { "INBOX" });
                    }
                    return Json(folders);
                }
                else
                {
                    // Für IMAP-Konten den bestehenden EmailService verwenden
                    var provider = await _providerFactory.GetServiceForAccountAsync(accountId);
                    var folders = await provider.GetMailFoldersAsync(accountId);
                    if (!folders.Any())
                    {
                        return Json(new List<string> { "INBOX" });
                    }
                    return Json(folders);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading folders for account {AccountId}", accountId);
                return Json(new List<string> { "INBOX" });
            }
        }

        // GET: MailAccounts/AuthorizeMsaDevice/5 — start Device Code Flow (no public URL required)
        [HttpGet]
        public async Task<IActionResult> AuthorizeMsaDevice(int id)
        {
            if (!await HasAccessToAccountAsync(id))
                return NotFound();

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
                return NotFound();

            // Note: we deliberately do NOT check account.Provider here. The user may have
            // switched the provider to MSA in the Edit form but not yet saved. The device-code
            // flow only needs a ClientId (per-account or default); the DB provider value is
            // irrelevant at this point and will be updated when the Edit form is saved.
            if (string.IsNullOrEmpty(account.ClientId) && !_msaOptions.HasDefaultClientId)
            {
                TempData["ErrorMessage"] = _localizer["MsaClientIdNotConfigured"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }

            try
            {
                // The service resolves the per-account ClientId, falling back to the configured default.
                var result = await _msaOAuthService.StartDeviceCodeAsync(account.ClientId);
                HttpContext.Session.SetString($"MsaDeviceCode_{id}", result.DeviceCode);
                // Persist the initial polling interval so the poll endpoint can adapt it on slow_down.
                HttpContext.Session.SetInt32($"MsaInterval_{id}", result.Interval);

                return View(new MsaDeviceCodeViewModel
                {
                    AccountId = id,
                    AccountName = account.Name,
                    UserCode = result.UserCode,
                    VerificationUri = result.VerificationUri,
                    ExpiresIn = result.ExpiresIn,
                    PollIntervalSeconds = result.Interval,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start MSA device code flow for account {AccountId}", id);
                TempData["ErrorMessage"] = string.Format(_localizer["MsaAuthorizationFailed"].Value, ex.Message);
                return RedirectToAction(nameof(Edit), new { id });
            }
        }

        // GET: MailAccounts/PollMsaDeviceCode?id=5 — called by browser JS every N seconds
        [HttpGet]
        public async Task<IActionResult> PollMsaDeviceCode(int id)
        {
            if (!await HasAccessToAccountAsync(id))
                return Json(new { status = "error", message = _localizer["MsaAccessDenied"].Value });

            var deviceCode = HttpContext.Session.GetString($"MsaDeviceCode_{id}");
            if (string.IsNullOrEmpty(deviceCode))
                return Json(new { status = "error", message = _localizer["MsaSessionExpired"].Value });

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
                return Json(new { status = "error", message = _localizer["MsaAccountNotFound"].Value });

            var currentInterval = HttpContext.Session.GetInt32($"MsaInterval_{id}") ?? 5;

            try
            {
                var poll = await _msaOAuthService.PollDeviceCodeAsync(account.ClientId, deviceCode, currentInterval);

                if (poll.Status == MsaPollStatus.Pending)
                {
                    return Json(new { status = "pending", interval = poll.IntervalSeconds });
                }
                if (poll.Status == MsaPollStatus.SlowDown)
                {
                    // RFC 8628 §3.5: increase the polling interval and keep polling.
                    HttpContext.Session.SetInt32($"MsaInterval_{id}", poll.IntervalSeconds);
                    return Json(new { status = "pending", interval = poll.IntervalSeconds });
                }

                // Success — save tokens
                HttpContext.Session.Remove($"MsaDeviceCode_{id}");
                HttpContext.Session.Remove($"MsaInterval_{id}");

                account.OAuthAccessToken = poll.Token!.AccessToken;
                account.OAuthRefreshToken = poll.Token.RefreshToken;
                account.OAuthTokenExpiry = poll.Token.Expiry;
                account.OAuthGrantedScopes = poll.Token.GrantedScopes;

                // Store the primary login name of the account that was actually authorized.
                // Outlook rejects XOAUTH2 when the SASL username is a secondary alias or does
                // not match the authorized account, so the id_token identity takes precedence
                // over the user-entered email address.
                if (!string.IsNullOrEmpty(poll.Token.AuthorizedUsername))
                {
                    if (!string.Equals(poll.Token.AuthorizedUsername, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "MSA account {AccountName}: authorized identity '{AuthorizedUsername}' differs from entered email address '{EmailAddress}'. Using authorized identity for IMAP authentication.",
                            account.Name, poll.Token.AuthorizedUsername, account.EmailAddress);
                    }
                    account.Username = poll.Token.AuthorizedUsername;
                }
                await _context.SaveChangesAsync();

                if (account.IsEnabled)
                    _onDemandSyncQueue.Enqueue(account.Id, MailSyncRequestPriority.Interactive, MailSyncRequestKind.ValidateConnection);

                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService?.GetCurrentUserDisplayName(HttpContext);
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: $"Authorized MSA account via device code: {account.Name}",
                        mailAccountId: account.Id);
                }

                return Json(new { status = "success" });
            }
            catch (MsaDeviceCodeTerminalException ex)
            {
                // Terminal OAuth errors (expired_token, access_denied, invalid_grant, ...):
                // the flow cannot continue — clear the session so the user must restart.
                _logger.LogWarning("MSA device code terminal error for account {AccountId}: {Code} — {Message}",
                    id, ex.ErrorCode, ex.Message);
                HttpContext.Session.Remove($"MsaDeviceCode_{id}");
                HttpContext.Session.Remove($"MsaInterval_{id}");
                return Json(new { status = "error", message = ex.Message });
            }
            catch (Exception ex)
            {
                // Transient failure (network blip, DNS, TLS, 5xx, non-JSON error page, ...):
                // keep the device code in session and report pending so the browser keeps polling.
                // The device code is still valid server-side; only the polling request failed.
                _logger.LogWarning(ex, "MSA device code transient poll error for account {AccountId}; will retry", id);
                return Json(new { status = "pending", interval = currentInterval });
            }
        }

        // GET: MailAccounts/CancelMsaAuthorization/5 — called by the Cancel button on the
        // device-code authorization page. Cleans up pending session state and returns to
        // the Edit page. The account is never deleted here.
        [HttpGet]
        public async Task<IActionResult> CancelMsaAuthorization(int id)
        {
            if (!await HasAccessToAccountAsync(id))
                return NotFound();

            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
                return RedirectToAction(nameof(Index));

            // Clean up any pending device code session state.
            HttpContext.Session.Remove($"MsaDeviceCode_{id}");
            HttpContext.Session.Remove($"MsaInterval_{id}");

            return RedirectToAction(nameof(Edit), new { id });
        }

        // Helper method to extract domain from email address
        private string ExtractDomain(string emailAddress)
        {
            if (string.IsNullOrWhiteSpace(emailAddress))
                return string.Empty;

            var atIndex = emailAddress.IndexOf('@');
            if (atIndex > 0 && atIndex < emailAddress.Length - 1)
            {
                return emailAddress.Substring(atIndex + 1).ToLowerInvariant();
            }
            return string.Empty;
        }

        // AJAX endpoint to check for existing M365 accounts with same domain
        [HttpGet]
        public async Task<JsonResult> CheckM365AccountsForDomain(string emailAddress, int? excludeAccountId = null)
        {
            try
            {
                var domain = ExtractDomain(emailAddress);
                if (string.IsNullOrEmpty(domain))
                {
                    return Json(new { exists = false });
                }

                // Get user info for permission checking
                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                var isAdmin = authService.IsCurrentUserAdmin(HttpContext);
                var isSelfManager = authService.IsCurrentUserSelfManager(HttpContext);

                IQueryable<MailAccount> accountsQuery = _context.MailAccounts
                    .Where(ma => ma.Provider == ProviderType.M365 &&
                                 ma.EmailAddress.ToLower().Contains("@" + domain) &&
                                 ma.ClientId != null);

                accountsQuery = accountsQuery
                    .Where(ma => ma.UserMailAccounts.Any(uma => uma.User.Username.ToLower() == currentUsername.ToLower()));

                // Exclude current account if editing
                if (excludeAccountId.HasValue)
                {
                    accountsQuery = accountsQuery.Where(ma => ma.Id != excludeAccountId.Value);
                }

                var existingAccounts = await accountsQuery
                    .Select(ma => new
                    {
                        id = ma.Id,
                        name = ma.Name,
                        emailAddress = ma.EmailAddress,
                        hasCredentials = !string.IsNullOrEmpty(ma.ClientId) &&
                                        !string.IsNullOrEmpty(ma.ClientSecret) &&
                                        !string.IsNullOrEmpty(ma.TenantId)
                    })
                    .ToListAsync();

                return Json(new
                {
                    exists = existingAccounts.Any(),
                    accounts = existingAccounts,
                    domain = domain
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking M365 accounts for email {EmailAddress}", emailAddress);
                return Json(new { exists = false, error = ex.Message });
            }
        }

        // AJAX endpoint to get M365 credentials from an existing account
        [HttpGet]
        public async Task<JsonResult> GetM365Credentials(int accountId)
        {
            try
            {
                // Check if user has access to this account
                if (!await HasAccessToAccountAsync(accountId))
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                var account = await _context.MailAccounts
                    .Where(ma => ma.Id == accountId && ma.Provider == ProviderType.M365)
                    .FirstOrDefaultAsync();

                if (account == null)
                {
                    return Json(new { success = false, message = "Account not found" });
                }

                return Json(new
                {
                    success = true,
                    clientId = account.ClientId,
                    tenantId = account.TenantId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting M365 credentials for account {AccountId}", accountId);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // AJAX endpoint to update M365 credentials for all accounts in a domain
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> UpdateM365CredentialsForDomain(int accountId, string clientId, string clientSecret, string tenantId)
        {
            try
            {
                // Check if user has access to the source account
                if (!await HasAccessToAccountAsync(accountId))
                {
                    return Json(new { success = false, message = "Access denied to source account" });
                }

                var sourceAccount = await _context.MailAccounts.FindAsync(accountId);
                if (sourceAccount == null || sourceAccount.Provider != ProviderType.M365)
                {
                    return Json(new { success = false, message = "Source account not found or not M365" });
                }

                var domain = ExtractDomain(sourceAccount.EmailAddress);
                if (string.IsNullOrEmpty(domain))
                {
                    return Json(new { success = false, message = "Could not extract domain from email" });
                }

                // Get user info for permission checking
                var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                var isAdmin = authService.IsCurrentUserAdmin(HttpContext);
                var isSelfManager = authService.IsCurrentUserSelfManager(HttpContext);

                // Get all M365 accounts with same domain
                IQueryable<MailAccount> accountsQuery = _context.MailAccounts
                    .Where(ma => ma.Provider == ProviderType.M365 &&
                                 ma.EmailAddress.ToLower().Contains("@" + domain) &&
                                 ma.Id != accountId); // Exclude the source account

                accountsQuery = accountsQuery
                    .Where(ma => ma.UserMailAccounts.Any(uma => uma.User.Username.ToLower() == currentUsername.ToLower()));

                var accountsToUpdate = await accountsQuery.ToListAsync();

                if (!accountsToUpdate.Any())
                {
                    return Json(new { success = true, updatedCount = 0, message = "No other accounts to update" });
                }

                // Update credentials
                int updatedCount = 0;
                foreach (var account in accountsToUpdate)
                {
                    account.ClientId = clientId;
                    if (!string.IsNullOrEmpty(clientSecret))
                    {
                        account.ClientSecret = clientSecret;
                    }
                    account.TenantId = tenantId;
                    updatedCount++;
                }

                await _context.SaveChangesAsync();

                // Log the bulk update action
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: $"Updated M365 credentials for {updatedCount} accounts in domain {domain}");
                }

                return Json(new
                {
                    success = true,
                    updatedCount = updatedCount,
                    message = $"Updated credentials for {updatedCount} account(s)",
                    accountNames = accountsToUpdate.Select(a => a.Name).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating M365 credentials for domain");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // Shared helper: lists tenant mailboxes and marks already-imported ones.
        private async Task<List<TenantMailboxViewModel>> GetTenantMailboxesWithExistingAsync(
            string clientId, string clientSecret, string tenantId, bool includeDisabled)
        {
            var tenantUsers = await _graphEmailService.GetTenantMailboxUsersAsync(
                clientId, clientSecret, tenantId, includeDisabled: includeDisabled);

            var tenantMailboxes = tenantUsers
                .Select(user => new
                {
                    DisplayName = string.IsNullOrWhiteSpace(user.DisplayName)
                        ? user.UserPrincipalName ?? user.Mail ?? _localizer["M365MailboxDefaultName"].Value
                        : user.DisplayName,
                    EmailAddress = string.IsNullOrWhiteSpace(user.Mail)
                        ? user.UserPrincipalName
                        : user.Mail,
                    IsDisabled = user.AccountEnabled == false
                })
                .Where(user => !string.IsNullOrWhiteSpace(user.EmailAddress))
                .GroupBy(user => user.EmailAddress!.Trim().ToLowerInvariant())
                .Select(group => group.First())
                .ToList();

            var mailboxAddresses = tenantMailboxes
                .Select(mailbox => mailbox.EmailAddress!.Trim().ToLowerInvariant())
                .ToList();

            var existingAddresses = await ManageableMailAccounts()
                .Where(account => account.Provider == ProviderType.M365 &&
                                  account.EmailAddress != null &&
                                  mailboxAddresses.Contains(account.EmailAddress.ToLower()))
                .Select(account => account.EmailAddress.ToLower())
                .ToListAsync();
            var existingAddressSet = existingAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return tenantMailboxes
                .Select(mailbox => new TenantMailboxViewModel
                {
                    DisplayName = mailbox.DisplayName,
                    EmailAddress = mailbox.EmailAddress!,
                    IsDisabled = mailbox.IsDisabled,
                    AlreadyExists = existingAddressSet.Contains(mailbox.EmailAddress!)
                })
                .OrderBy(m => m.AlreadyExists)
                .ThenBy(m => m.EmailAddress, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // GET: MailAccounts/TenantManagement/5
        public async Task<IActionResult> TenantManagement(int id)
        {
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            if (authService == null || !authService.IsAuthenticated(HttpContext))
            {
                return Forbid();
            }

            if (!await HasAccessToAccountAsync(id))
            {
                return NotFound();
            }

            var sourceAccount = await _context.MailAccounts
                .Where(a => a.Id == id && a.Provider == ProviderType.M365)
                .FirstOrDefaultAsync();

            if (sourceAccount == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(sourceAccount.ClientId) ||
                string.IsNullOrWhiteSpace(sourceAccount.ClientSecret) ||
                string.IsNullOrWhiteSpace(sourceAccount.TenantId))
            {
                _logger.LogWarning("Source account {AccountId} has incomplete M365 credentials", id);
                var missingModel = new TenantManagementViewModel
                {
                    SourceAccountId = sourceAccount.Id,
                    SourceAccountName = sourceAccount.Name,
                    SourceEmailAddress = sourceAccount.EmailAddress,
                    ErrorMessage = _localizer["M365TenantCredentialsRequired"].Value
                };
                return View(missingModel);
            }

            var sourceName = sourceAccount.Name ?? string.Empty;
            var separatorIndex = sourceName.IndexOf(" - <");
            var defaultPrefix = separatorIndex >= 0
                ? sourceName.Substring(0, separatorIndex).Trim()
                : sourceName.Trim();

            var model = new TenantManagementViewModel
            {
                SourceAccountId = sourceAccount.Id,
                SourceAccountName = sourceAccount.Name,
                SourceEmailAddress = sourceAccount.EmailAddress,
                Name = defaultPrefix
            };

            try
            {
                model.Mailboxes = await GetTenantMailboxesWithExistingAsync(
                    sourceAccount.ClientId!,
                    sourceAccount.ClientSecret!,
                    sourceAccount.TenantId!,
                    includeDisabled: false);

                _logger.LogInformation("Loaded {Count} tenant mailboxes for account {AccountId}",
                    model.Mailboxes.Count, id);

                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: $"Listed {model.Mailboxes.Count} M365 tenant mailboxes for source account {sourceAccount.EmailAddress}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant mailboxes for account {AccountId}", id);
                model.ErrorMessage = _localizer["M365TenantMailboxesCouldNotBeListed"].Value;
            }

            return View(model);
        }

        // POST: MailAccounts/AddTenantMailboxes
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTenantMailboxes(TenantManagementViewModel model)
        {
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            if (authService == null || !authService.IsAuthenticated(HttpContext))
            {
                return Forbid();
            }

            if (!await HasAccessToAccountAsync(model.SourceAccountId))
            {
                return NotFound();
            }

            var sourceAccount = await _context.MailAccounts
                .Where(a => a.Id == model.SourceAccountId && a.Provider == ProviderType.M365)
                .FirstOrDefaultAsync();

            if (sourceAccount == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(sourceAccount.ClientId) ||
                string.IsNullOrWhiteSpace(sourceAccount.ClientSecret) ||
                string.IsNullOrWhiteSpace(sourceAccount.TenantId))
            {
                ModelState.AddModelError("", _localizer["M365TenantCredentialsRequired"].Value);
                await PopulateMailboxesForPostErrorAsync(model, sourceAccount);
                return View("TenantManagement", model);
            }

            var maxSelected = _tenantManagementOptions.MaxSelectedMailboxes;
            if (maxSelected > 0 && model.SelectedMailboxes != null && model.SelectedMailboxes.Count > maxSelected)
            {
                ModelState.AddModelError("SelectedMailboxes",
                    _localizer["SelectAtMostMailboxes", maxSelected].Value);
            }

            if (!ModelState.IsValid)
            {
                await PopulateMailboxesForPostErrorAsync(model, sourceAccount);
                return View("TenantManagement", model);
            }

            try
            {
                var currentUserId = authService.GetCurrentUserId(HttpContext);
                if (!currentUserId.HasValue)
                {
                    return Forbid();
                }

                // Re-fetch the tenant list to validate the selected addresses against it.
                var tenantMailboxes = await GetTenantMailboxesWithExistingAsync(
                    sourceAccount.ClientId!,
                    sourceAccount.ClientSecret!,
                    sourceAccount.TenantId!,
                    includeDisabled: false);

                var tenantAddressSet = tenantMailboxes
                    .Select(m => m.EmailAddress.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var selectedNormalized = model.SelectedMailboxes
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Only accept addresses that are actually part of the tenant and not already imported.
                var toAdd = tenantMailboxes
                    .Where(m => selectedNormalized.Contains(m.EmailAddress.Trim().ToLowerInvariant()) && !m.AlreadyExists)
                    .ToList();

                if (toAdd.Count == 0 && !model.RenameExistingAccounts)
                {
                    TempData["ErrorMessage"] = _localizer["NoNewMailboxesToAdd"].Value;
                    return RedirectToAction(nameof(TenantManagement), new { id = model.SourceAccountId });
                }

                var accountNamePrefix = model.Name!.Trim();
                var addedCount = 0;
                var renamedCount = 0;

                if (toAdd.Count > 0)
                {
                    var accountsToCreate = toAdd.Select(mailbox => new MailAccount
                    {
                        Name = $"{accountNamePrefix} - <{mailbox.EmailAddress}>",
                        EmailAddress = mailbox.EmailAddress,
                        UseSSL = sourceAccount.UseSSL,
                        IsEnabled = true,
                        Provider = ProviderType.M365,
                        ClientId = sourceAccount.ClientId,
                        ClientSecret = sourceAccount.ClientSecret,
                        TenantId = sourceAccount.TenantId,
                        ExcludedFolders = string.Empty,
                        DeleteAfterDays = model.DeleteAfterDays,
                        LocalRetentionDays = model.LocalRetentionDays,
                        LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    }).ToList();

                    _context.MailAccounts.AddRange(accountsToCreate);
                    await _context.SaveChangesAsync();
                    addedCount = accountsToCreate.Count;

                    // Assign every imported tenant account to the importing user.
                    _context.UserMailAccounts.AddRange(accountsToCreate.Select(account => new UserMailAccount
                    {
                        UserId = currentUserId.Value,
                        MailAccountId = account.Id
                    }));
                    await _context.SaveChangesAsync();
                }

                if (model.RenameExistingAccounts)
                {
                    var accountsToRename = await _context.MailAccounts
                        .Where(a => a.Provider == ProviderType.M365
                                 && a.ClientId == sourceAccount.ClientId
                                 && a.TenantId == sourceAccount.TenantId
                                 && a.UserMailAccounts.Any(ownership => ownership.UserId == currentUserId.Value)
                                 && a.EmailAddress != null)
                        .ToListAsync();

                    foreach (var account in accountsToRename)
                    {
                        account.Name = $"{accountNamePrefix} - <{account.EmailAddress}>";
                    }
                    await _context.SaveChangesAsync();
                    renamedCount = accountsToRename.Count;

                    _logger.LogInformation("Renamed {Count} M365 accounts to schema '{Prefix} - <email>' for app registration {ClientId}",
                        renamedCount, accountNamePrefix, sourceAccount.ClientId);
                }

                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);

                if (!string.IsNullOrEmpty(currentUsername))
                {
                    var logParts = new List<string>();
                    if (addedCount > 0)
                    {
                        logParts.Add($"Added {addedCount} M365 tenant mailboxes via Tenant Management from source account {sourceAccount.EmailAddress}");
                    }
                    if (renamedCount > 0)
                    {
                        logParts.Add($"Renamed {renamedCount} M365 accounts to schema '{accountNamePrefix} - <email>' for app registration {sourceAccount.ClientId}");
                    }
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: string.Join("; ", logParts));
                }

                if (addedCount > 0 && renamedCount > 0)
                {
                    TempData["SuccessMessage"] = _localizer["MailboxesAddedAndAccountsRenamed", addedCount, renamedCount].Value;
                }
                else if (addedCount > 0)
                {
                    TempData["SuccessMessage"] = _localizer["MailboxesAdded", addedCount].Value;
                }
                else if (renamedCount > 0)
                {
                    TempData["SuccessMessage"] = _localizer["AccountsRenamed", renamedCount].Value;
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding tenant mailboxes from source account {AccountId}", model.SourceAccountId);
                ModelState.AddModelError("", _localizer["M365TenantAccountsCouldNotBeImported"].Value);
                await PopulateMailboxesForPostErrorAsync(model, sourceAccount);
                return View("TenantManagement", model);
            }
        }

        // Rebuilds the mailbox list for the view when a POST validation fails.
        private async Task PopulateMailboxesForPostErrorAsync(TenantManagementViewModel model, MailAccount sourceAccount)
        {
            model.SourceAccountName = sourceAccount.Name;
            model.SourceEmailAddress = sourceAccount.EmailAddress;
            try
            {
                model.Mailboxes = await GetTenantMailboxesWithExistingAsync(
                    sourceAccount.ClientId!,
                    sourceAccount.ClientSecret!,
                    sourceAccount.TenantId!,
                    includeDisabled: false);

                // Preserve the user's selection state on the rendered checkboxes.
                var selectedSet = (model.SelectedMailboxes ?? new List<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var mailbox in model.Mailboxes)
                {
                    if (selectedSet.Contains(mailbox.EmailAddress.Trim().ToLowerInvariant()) && !mailbox.AlreadyExists)
                    {
                        // Marking via a transient flag is not available; the view relies on SelectedMailboxes.
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading tenant mailboxes after POST validation failure");
                model.Mailboxes = new List<TenantMailboxViewModel>();
                model.ErrorMessage = _localizer["M365TenantMailboxesCouldNotBeListed"].Value;
            }
        }

        // GET: MailAccounts/ImportCsv
        [HttpGet]
        public IActionResult ImportCsv()
        {
            var model = new BulkImportImapViewModel
            {
                ImapPort = 993,
                UseSSL = true,
                IsEnabled = true,
                SkipExisting = true
            };
            return View(model);
        }

        // POST: MailAccounts/ImportCsv
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportCsv(BulkImportImapViewModel model)
        {
            var authService = HttpContext.RequestServices.GetService<MailArchiver.Services.IAuthenticationService>();
            var currentUserId = authService?.GetCurrentUserId(HttpContext);
            if (!currentUserId.HasValue)
            {
                return Forbid();
            }

            var accountFiles = model.AccountFiles ?? [];
            if (accountFiles.Count == 0)
                ModelState.AddModelError(nameof(model.AccountFiles), "请至少选择一个 TXT 或 CSV 文件。");
            if (accountFiles.Count > MaxAccountImportFiles)
                ModelState.AddModelError(nameof(model.AccountFiles), $"一次最多选择 {MaxAccountImportFiles} 个文件。");

            foreach (var file in accountFiles)
            {
                var safeFileName = Path.GetFileName(file.FileName);
                var extension = Path.GetExtension(safeFileName);
                if (!extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(nameof(model.AccountFiles), $"{safeFileName}：仅支持 TXT 和 CSV 文件。");
                }
                if (file.Length == 0)
                    ModelState.AddModelError(nameof(model.AccountFiles), $"{safeFileName}：文件为空。");
                else if (file.Length > _csvImportOptions.MaxFileSizeBytes)
                {
                    ModelState.AddModelError(nameof(model.AccountFiles),
                        $"{safeFileName}：" + _localizer["CsvImportFileTooLarge",
                            Math.Round(file.Length / 1_000_000.0, 1),
                            Math.Round(_csvImportOptions.MaxFileSizeBytes / 1_000_000.0, 1)].Value);
                }
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var result = new CsvImportResultViewModel();

            try
            {
                var rows = new List<CsvParsedRow>();
                var failedRows = new List<CsvImportFailedRow>();

                foreach (var file in accountFiles)
                {
                    var parsedFile = await ParseAccountImportFileAsync(file, model);
                    rows.AddRange(parsedFile.Rows);
                    failedRows.AddRange(parsedFile.FailedRows);
                }

                result.FailedRows.AddRange(failedRows);
                result.FailedCount = failedRows.Count;

                if (rows.Count == 0)
                {
                    result.FailedRows.Insert(0, new CsvImportFailedRow
                    {
                        LineNumber = 0,
                        Email = string.Empty,
                        Reason = _localizer["CsvImportNoValidRows"].Value
                    });
                    result.FailedCount = result.FailedRows.Count;
                    return View("CsvImportResult", result);
                }

                if (rows.Count > _csvImportOptions.MaxRows)
                {
                    result.FailedRows.Insert(0, new CsvImportFailedRow
                    {
                        LineNumber = 0,
                        Email = string.Empty,
                        Reason = _localizer["CsvImportTooManyRows", rows.Count, _csvImportOptions.MaxRows].Value
                    });
                    result.FailedCount = result.FailedRows.Count;
                    return View("CsvImportResult", result);
                }

                var dedupedRows = rows
                    .GroupBy(r => $"{r.Provider}:{r.Email!.Trim().ToLowerInvariant()}")
                    .Select(g => g.First())
                    .ToList();

                var dedupSkipped = rows.Count - dedupedRows.Count;
                if (dedupSkipped > 0)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rows)
                    {
                        var key = $"{row.Provider}:{row.Email!.Trim().ToLowerInvariant()}";
                        if (!seen.Add(key))
                        {
                            result.SkippedRows.Add(new CsvImportSkippedRow
                            {
                                Email = row.Email,
                                Reason = _localizer["CsvImportDuplicateInFile"].Value
                            });
                        }
                    }
                }

                var incomingEmails = dedupedRows
                    .Select(r => r.Email!.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var existingEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                const int chunkSize = 500;
                var emailList = incomingEmails.ToList();
                for (int i = 0; i < emailList.Count; i += chunkSize)
                {
                    var chunk = emailList.Skip(i).Take(chunkSize).ToList();
                    var matches = await _context.MailAccounts
                        .Where(a =>
                            (a.Provider == ProviderType.IMAP || a.Provider == ProviderType.MSA) &&
                            chunk.Contains(a.EmailAddress.ToLower()) &&
                            a.UserMailAccounts.Any(ownership => ownership.UserId == currentUserId.Value))
                        .Select(a => a.EmailAddress.ToLower())
                        .ToListAsync();
                    foreach (var m in matches)
                    {
                        existingEmails.Add(m);
                    }
                }

                var accountsToCreate = new List<MailAccount>();

                foreach (var row in dedupedRows)
                {
                    var emailLower = row.Email!.Trim().ToLowerInvariant();
                    if (existingEmails.Contains(emailLower))
                    {
                        if (model.SkipExisting)
                        {
                            result.SkippedRows.Add(new CsvImportSkippedRow
                            {
                                Email = row.Email,
                                Reason = _localizer["CsvImportAlreadyExists"].Value
                            });
                        }
                        else
                        {
                            result.FailedRows.Add(new CsvImportFailedRow
                            {
                                FileName = row.SourceFileName,
                                LineNumber = row.LineNumber,
                                Email = row.Email,
                                Reason = _localizer["CsvImportAlreadyExists"].Value
                            });
                            result.FailedCount++;
                        }
                        continue;
                    }

                    IMailProviderModule rowModule;
                    try
                    {
                        rowModule = _mailProviderRegistry.For(row.MailProviderKind);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                    {
                        result.FailedRows.Add(new CsvImportFailedRow
                        {
                            FileName = row.SourceFileName,
                            LineNumber = row.LineNumber,
                            Email = row.Email,
                            Reason = ex.Message
                        });
                        result.FailedCount++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(row.Password))
                    {
                        try
                        {
                            row.Password = rowModule.NormalizeAppPassword(row.Password);
                        }
                        catch (InvalidOperationException ex)
                        {
                            result.FailedRows.Add(new CsvImportFailedRow
                            {
                                FileName = row.SourceFileName,
                                LineNumber = row.LineNumber,
                                Email = row.Email,
                                Reason = ex.Message
                            });
                            result.FailedCount++;
                            continue;
                        }
                    }

                    var account = row.Provider == ProviderType.MSA
                        && !string.IsNullOrWhiteSpace(row.ClientId)
                        && !string.IsNullOrWhiteSpace(row.OAuthRefreshToken)
                        ? OutlookImportedAccountFactory.Create(
                            new OutlookImportedAccount(
                                row.LineNumber,
                                row.Email,
                                row.ClientId!,
                                row.OAuthRefreshToken!),
                            model.IsEnabled,
                            string.IsNullOrWhiteSpace(row.Password)
                                ? null
                                : _credentialEncryptionService.Encrypt(row.Password))
                        : new MailAccount
                        {
                            Name = MailAccountNamePolicy.Derive(row.Email),
                            EmailAddress = row.Email!.Trim(),
                            GroupName = string.Empty,
                            ImapServer = null,
                            ImapPort = null,
                            Username = row.Email.Trim(),
                            Password = string.IsNullOrWhiteSpace(row.Password)
                                ? null
                                : _credentialEncryptionService.Encrypt(row.Password),
                            ClientId = row.ClientId,
                            ClientSecret = row.ClientSecret,
                            OAuthRefreshToken = row.OAuthRefreshToken,
                            OAuthGrantedScopes = row.OAuthGrantedScopes,
                            OAuthRedirectUri = row.OAuthRedirectUri,
                            UseSSL = true,
                            IsEnabled = model.IsEnabled,
                            Provider = row.Provider,
                            ExcludedFolders = string.Empty,
                            DeleteAfterDays = null,
                            LocalRetentionDays = null,
                            LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        };

                    rowModule.PrepareAccount(account);

                    accountsToCreate.Add(account);
                    result.CreatedRows.Add(new CsvImportCreatedRow
                    {
                        Email = account.EmailAddress,
                        Name = account.Name
                    });
                }

                if (accountsToCreate.Count == 0)
                {
                    result.CreatedCount = 0;
                    result.SkippedCount = result.SkippedRows.Count;
                    _logger.LogInformation("CSV bulk import produced no new accounts (all skipped or failed)");
                    return View("CsvImportResult", result);
                }

                _context.MailAccounts.AddRange(accountsToCreate);
                await _context.SaveChangesAsync();
                _context.UserMailAccounts.AddRange(accountsToCreate.Select(account => new UserMailAccount
                {
                    UserId = currentUserId.Value,
                    MailAccountId = account.Id
                }));
                await _context.SaveChangesAsync();
                foreach (var account in accountsToCreate.Where(account => account.IsEnabled))
                {
                    _onDemandSyncQueue.Enqueue(account.Id, MailSyncRequestPriority.Bulk, MailSyncRequestKind.ValidateConnection);
                }

                result.CreatedCount = accountsToCreate.Count;
                result.SkippedCount = result.SkippedRows.Count;

                var currentUsername = authService.GetCurrentUserDisplayName(HttpContext);
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    await _accessLogService.LogAccessAsync(currentUsername, AccessLogType.Account,
                        searchParameters: $"Account bulk import: {result.CreatedCount} accounts created, {result.SkippedCount} skipped, {result.FailedCount} failed");
                }

                _logger.LogInformation("CSV bulk import completed: {Created} created, {Skipped} skipped, {Failed} failed",
                    result.CreatedCount, result.SkippedCount, result.FailedCount);

                return View("CsvImportResult", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing CSV bulk import");
                ModelState.AddModelError("", $"{_localizer["CsvImportCouldNotBeProcessed"]}: {ex.Message}");
                return View(model);
            }
        }

        private async Task<AccountImportFileParseResult> ParseAccountImportFileAsync(
            IFormFile file,
            BulkImportImapViewModel model)
        {
            var fileName = Path.GetFileName(file.FileName);
            var rows = new List<CsvParsedRow>();
            var failedRows = new List<CsvImportFailedRow>();

            string uploadedText;
            using (var stream = file.OpenReadStream())
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
            {
                uploadedText = await reader.ReadToEndAsync();
            }

            var firstNonEmptyLine = uploadedText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

            if (firstNonEmptyLine?.Contains('\t') == true &&
                !firstNonEmptyLine.Split('\t')[0].Trim().Equals("邮箱", StringComparison.OrdinalIgnoreCase) &&
                !firstNonEmptyLine.Split('\t')[0].Trim().Equals("email", StringComparison.OrdinalIgnoreCase))
            {
                using var unifiedReader = new StringReader(uploadedText);
                var parsed = UnifiedMailAccountTextParser.Parse(unifiedReader);
                rows.AddRange(parsed.Accounts.Select(account => new CsvParsedRow
                {
                    LineNumber = account.LineNumber,
                    Email = account.Email,
                    Provider = account.Provider == MailProviderKind.Outlook ? ProviderType.MSA : ProviderType.IMAP,
                    MailProviderKind = account.Provider,
                    Password = account.AppPassword ?? string.Empty,
                    ClientId = account.ClientId,
                    ClientSecret = account.ClientSecret,
                    OAuthRefreshToken = account.RefreshToken,
                    OAuthRedirectUri = account.RedirectUri
                }));
                failedRows.AddRange(parsed.Errors.Select(error => new CsvImportFailedRow
                {
                    LineNumber = error.LineNumber,
                    Email = string.Empty,
                    Reason = error.Reason
                }));
            }
            else
            {
                var lineNumber = 0;
                var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                using var csvReader = new StringReader(uploadedText);
                using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(csvReader);
                parser.SetDelimiters(firstNonEmptyLine?.Contains('\t') == true ? "\t" : ",");
                parser.HasFieldsEnclosedInQuotes = true;

                while (!parser.EndOfData)
                {
                    lineNumber++;
                    string[]? fields;

                    try
                    {
                        fields = parser.ReadFields();
                    }
                    catch (Microsoft.VisualBasic.FileIO.MalformedLineException exception)
                    {
                        failedRows.Add(new CsvImportFailedRow
                        {
                            LineNumber = lineNumber,
                            Email = string.Empty,
                            Reason = _localizer["CsvImportMalformedLine"].Value
                        });
                        _logger.LogWarning(exception, "Malformed account import line {LineNumber} in {FileName}", lineNumber, fileName);
                        continue;
                    }

                    if (fields is null)
                        continue;

                    if (lineNumber == 1)
                    {
                        var headers = fields.Select(field => field?.Trim() ?? string.Empty).ToArray();
                        if (headers.Any(header => header.Length > 0))
                        {
                            if (!CsvImportHeaderPolicy.TryCreateCanonicalIndex(headers, out headerIndex))
                            {
                                failedRows.Add(new CsvImportFailedRow
                                {
                                    LineNumber = lineNumber,
                                    Email = string.Empty,
                                    Reason = "文件必须包含邮箱，以及应用专用密码；或 Client ID、Refresh Token（Yahoo 还需 Client Secret）。"
                                });
                                break;
                            }
                            continue;
                        }
                    }

                    var row = ParseCsvRow(fields, headerIndex, model, lineNumber, failedRows, _localizer);
                    if (row is not null)
                        rows.Add(row);
                }
            }

            foreach (var row in rows)
                row.SourceFileName = fileName;
            foreach (var failure in failedRows)
                failure.FileName = fileName;

            return new AccountImportFileParseResult(rows, failedRows);
        }

        private sealed record AccountImportFileParseResult(
            List<CsvParsedRow> Rows,
            List<CsvImportFailedRow> FailedRows);

        // GET: MailAccounts/DownloadExampleCsv
        [HttpGet]
        public IActionResult DownloadExampleCsv()
        {
            var csv = "邮箱,SMTP授权码,Client ID,Client Secret,Refresh Token,Redirect URI\r\n"
                + "carol@gmail.com,abcd efgh ijkl mnop,,,,\r\n"
                + "alice@gmx.com,示例应用专用密码,,,,\r\n"
                + "bob123@yahoo.com,示例应用专用密码,,,,\r\n"
                + "oauth@gmail.com,,示例GoogleClientID,,示例RefreshToken,\r\n"
                + "both@gmail.com,abcd efgh ijkl mnop,示例GoogleClientID,,示例RefreshToken,\r\n"
                + "outlook@outlook.com,,11111111-2222-3333-4444-555555555555,,示例RefreshToken,\r\n"
                + "david@yahoo.com,,示例YahooClientID,示例YahooClientSecret,示例RefreshToken,oob\r\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
            return File(bytes, "text/csv", "邮箱批量导入示例.csv");
        }

        [HttpGet]
        public IActionResult DownloadExampleTxt()
        {
            var text = "carol@gmail.com\tabcd efgh ijkl mnop\r\n"
                + "alice@gmx.com\t示例应用专用密码\r\n"
                + "bob@yahoo.com\t示例应用专用密码\r\n"
                + "outlook@outlook.com\t密码回退示例\t11111111-2222-3333-4444-555555555555\t示例RefreshToken\r\n"
                + "oauth@gmail.com\t示例GoogleClientID\t示例RefreshToken\r\n"
                + "both@gmail.com\tabcd efgh ijkl mnop\t示例GoogleClientID\t\t示例RefreshToken\t\r\n"
                + "david@yahoo.com\t示例YahooClientID\t示例YahooClientSecret\t示例RefreshToken\toob\r\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            return File(bytes, "text/plain", "邮箱批量导入示例.txt");
        }

        private CsvParsedRow? ParseCsvRow(string[] fields, Dictionary<string, int> headerIndex,
            BulkImportImapViewModel model, int lineNumber, List<CsvImportFailedRow> failedRows,
            IStringLocalizer<SharedResource> localizer)
        {
            string email = string.Empty;
            string password = string.Empty;
            string? clientId = null;
            string? clientSecret = null;
            string? refreshToken = null;
            string? scopes = null;
            string? redirectUri = null;
            string? username = null;
            string? imapServer = null;
            int? imapPort = null;
            bool? useSsl = null;

            if (headerIndex.Count > 0)
            {
                email = GetFieldValue(fields, headerIndex, "email");
                password = GetFieldValueRaw(fields, headerIndex, "app_password");
                clientId = GetFieldValueOrNull(fields, headerIndex, "client_id");
                clientSecret = GetFieldValueOrNull(fields, headerIndex, "client_secret");
                refreshToken = GetFieldValueOrNull(fields, headerIndex, "refresh_token");
                scopes = GetFieldValueOrNull(fields, headerIndex, "scopes");
                redirectUri = GetFieldValueOrNull(fields, headerIndex, "redirect_uri");
                username = null;
                imapServer = null;
                var portStr = GetFieldValueOrNull(fields, headerIndex, "imap_port");
                if (!string.IsNullOrWhiteSpace(portStr))
                {
                    if (int.TryParse(portStr.Trim(), out var port) && port >= 1 && port <= 65535)
                    {
                        imapPort = port;
                    }
                    else
                    {
                        failedRows.Add(new CsvImportFailedRow
                        {
                            LineNumber = lineNumber,
                            Email = email,
                            Reason = localizer["CsvImportInvalidPort", lineNumber, portStr].Value
                        });
                        return null;
                    }
                }
                var sslStr = GetFieldValueOrNull(fields, headerIndex, "use_ssl");
                if (!string.IsNullOrWhiteSpace(sslStr))
                {
                    if (bool.TryParse(sslStr.Trim(), out var ssl))
                    {
                        useSsl = ssl;
                    }
                    else
                    {
                        failedRows.Add(new CsvImportFailedRow
                        {
                            LineNumber = lineNumber,
                            Email = email,
                            Reason = localizer["CsvImportInvalidUseSsl", lineNumber, sslStr].Value
                        });
                        return null;
                    }
                }
            }
            else
            {
                failedRows.Add(new CsvImportFailedRow
                {
                    LineNumber = lineNumber,
                    Email = string.Empty,
                    Reason = "CSV 缺少表头。"
                });
                return null;
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                failedRows.Add(new CsvImportFailedRow
                {
                    LineNumber = lineNumber,
                    Email = string.Empty,
                    Reason = localizer["CsvImportMissingEmail", lineNumber].Value
                });
                return null;
            }

            IMailProviderModule providerModule;
            try
            {
                providerModule = _mailProviderRegistry.Detect(email);
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
            {
                failedRows.Add(new CsvImportFailedRow
                {
                    LineNumber = lineNumber,
                    Email = email,
                    Reason = exception.Message
                });
                return null;
            }

            var hasPassword = !string.IsNullOrWhiteSpace(password);
            var hasAnyOAuthValue = !string.IsNullOrWhiteSpace(clientId)
                || !string.IsNullOrWhiteSpace(clientSecret)
                || !string.IsNullOrWhiteSpace(refreshToken);
            if (!hasPassword && !hasAnyOAuthValue)
            {
                failedRows.Add(new CsvImportFailedRow
                {
                    LineNumber = lineNumber,
                    Email = email,
                    Reason = "必须提供应用专用密码，或完整 OAuth 凭据。"
                });
                return null;
            }

            if (providerModule.Kind == MailProviderKind.Outlook)
            {
                var hasCompleteOAuth = !string.IsNullOrWhiteSpace(clientId)
                    && Guid.TryParseExact(clientId.Trim(), "D", out _)
                    && !string.IsNullOrWhiteSpace(refreshToken);
                var hasPartialOAuth = !string.IsNullOrWhiteSpace(clientId)
                    || !string.IsNullOrWhiteSpace(refreshToken)
                    || !string.IsNullOrWhiteSpace(clientSecret);
                if (hasPartialOAuth && !hasCompleteOAuth)
                {
                    failedRows.Add(new CsvImportFailedRow
                    {
                        LineNumber = lineNumber,
                        Email = email,
                        Reason = "Outlook OAuth 必须同时提供格式正确的 Client ID 和 Refresh Token；否则请只提供密码走 IMAP/SMTP。"
                    });
                    return null;
                }

                // Keep the password encrypted as a final IMAP/SMTP fallback when OAuth is unavailable.
            }
            else if (hasAnyOAuthValue)
            {
                if (!ExternalOAuthProviderPolicy.TryResolve(email, out var oauthProvider))
                {
                    failedRows.Add(new CsvImportFailedRow
                    {
                        LineNumber = lineNumber,
                        Email = email,
                        Reason = "GMX 官方不支持 OAuth，请提供应用专用密码。"
                    });
                    return null;
                }
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken) ||
                    (oauthProvider.RequiresClientSecret && string.IsNullOrWhiteSpace(clientSecret)) ||
                    (oauthProvider.RequiresRedirectUri && string.IsNullOrWhiteSpace(redirectUri)))
                {
                    failedRows.Add(new CsvImportFailedRow
                    {
                        LineNumber = lineNumber,
                        Email = email,
                        Reason = oauthProvider.RequiresClientSecret
                            ? "Yahoo OAuth 必须提供 Client ID、Client Secret、Refresh Token 和 Redirect URI。"
                            : "Gmail OAuth 必须提供 Client ID 和 Refresh Token。"
                    });
                    return null;
                }
            }

            return new CsvParsedRow
            {
                LineNumber = lineNumber,
                Email = email,
                Password = password,
                Provider = providerModule.Kind == MailProviderKind.Outlook ? ProviderType.MSA : ProviderType.IMAP,
                MailProviderKind = providerModule.Kind,
                ClientId = clientId,
                ClientSecret = clientSecret,
                OAuthRefreshToken = refreshToken,
                OAuthGrantedScopes = scopes,
                OAuthRedirectUri = redirectUri,
                Username = username,
                ImapServer = imapServer,
                ImapPort = imapPort,
                UseSSL = useSsl
            };
        }

        private static string GetFieldValue(string[] fields, Dictionary<string, int> headerIndex, string name)
        {
            if (headerIndex.TryGetValue(name, out var idx) && idx < fields.Length)
            {
                return fields[idx]?.Trim() ?? string.Empty;
            }
            return string.Empty;
        }

        private static string GetFieldValueRaw(string[] fields, Dictionary<string, int> headerIndex, string name)
        {
            if (headerIndex.TryGetValue(name, out var idx) && idx < fields.Length)
            {
                return fields[idx] ?? string.Empty;
            }
            return string.Empty;
        }

        private static string? GetFieldValueOrNull(string[] fields, Dictionary<string, int> headerIndex, string name)
        {
            if (headerIndex.TryGetValue(name, out var idx) && idx < fields.Length)
            {
                var val = fields[idx];
                return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
            }
            return null;
        }
    }
}
