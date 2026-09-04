using System.Text.Json;

namespace MailArchiver.Tests.LocalApp;

public class LocalAppPackagingPolicyTests
{
    [Fact]
    public void LocalAppConfiguration_configures_automatic_mail_sync()
    {
        using var document = JsonDocument.Parse(ReadBundledFile("appsettings.Local.json"));
        var mailSync = document.RootElement.GetProperty("MailSync");

        Assert.True(mailSync.GetProperty("SyncInboxOnly").GetBoolean());
        Assert.True(mailSync.GetProperty("MaxConcurrentSyncs").GetInt32() > 0);
    }

    [Fact]
    public void LocalAppConfiguration_enables_outlook_sending_with_bounded_attachments()
    {
        using var document = JsonDocument.Parse(ReadBundledFile("appsettings.Local.json"));
        var outbound = document.RootElement.GetProperty("OutboundMail");

        Assert.True(outbound.GetProperty("Enabled").GetBoolean());
        Assert.Equal(10, outbound.GetProperty("MaxAttachmentCount").GetInt32());
        Assert.Equal(10 * 1024 * 1024, outbound.GetProperty("MaxTotalAttachmentBytes").GetInt64());
    }

    [Fact]
    public void LocalAppConfiguration_caps_each_mailbox_at_thirty_messages()
    {
        using var document = JsonDocument.Parse(ReadBundledFile("appsettings.Local.json"));

        Assert.Equal(30, document.RootElement
            .GetProperty("MailSync")
            .GetProperty("MaxStoredEmailsPerAccount")
            .GetInt32());
    }

    [Fact]
    public void LocalAppStartup_disables_the_archive_wide_storage_backfill()
    {
        var source = ReadBundledFile("Program.cs");

        Assert.Contains("if (isLocalApp)", source, StringComparison.Ordinal);
        Assert.Contains(
            "builder.Configuration[\"AccountStorage:Enabled\"] = \"false\";",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Application_normalizes_storage_to_UTC_and_displays_Beijing_time()
    {
        using var document = JsonDocument.Parse(ReadBundledFile("appsettings.json"));
        var timeZone = document.RootElement.GetProperty("TimeZone");
        var layout = ReadBundledFile("Layout.cshtml");

        Assert.Equal("Etc/UTC", timeZone.GetProperty("StorageTimeZoneId").GetString());
        Assert.Equal("Asia/Shanghai", timeZone.GetProperty("DisplayTimeZoneId").GetString());
        Assert.Contains("timeZone: displayTimeZone", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_account_entry_detects_provider_in_both_ui_and_server()
    {
        var page = ReadBundledFile("MailAccountsCreate.cshtml");
        var controller = ReadBundledFile("MailAccountsController.cs");

        Assert.Contains("输入邮箱地址后自动识别", page, StringComparison.Ordinal);
        Assert.Contains("applyLocalProviderDetection()", page, StringComparison.Ordinal);
        Assert.Contains("使用 OAuth（高级）", page, StringComparison.Ordinal);
        Assert.Contains("_mailProviderRegistry.Detect(model.EmailAddress", controller, StringComparison.Ordinal);
        Assert.Contains("model.Provider = mailProviderModule.Kind == MailProviderKind.Outlook", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_synchronized_mailbox_provider_enforces_the_local_message_cap()
    {
        Assert.Contains(
            "EnforceLocalEmailLimitAsync(account.Id)",
            ReadBundledFile("ImapMailSyncService.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "EnforceLocalEmailLimitAsync(account.Id)",
            ReadBundledFile("GraphMailSyncService.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAppRelease_version_identifies_the_multiple_account_file_import()
    {
        var source = ReadBundledFile("Info.plist");

        Assert.Contains("<string>2.0.0</string>", source, StringComparison.Ordinal);
        Assert.Contains("<string>200</string>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRelease_matches_the_macOS_feature_version()
    {
        var source = ReadBundledFile("KouziMailAssistant.Windows.csproj");

        Assert.Contains("<Version>2.0.0</Version>", source, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>2.0.0.0</FileVersion>", source, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>AppIcon.ico</ApplicationIcon>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_mail_send_treats_cc_and_attachments_as_truly_optional()
    {
        var model = ReadBundledFile("ComposeMailViewModel.cs");
        var controller = ReadBundledFile("OutboundMailController.cs");
        var page = ReadBundledFile("OutboundMailIndex.cshtml");

        Assert.Contains("string? Cc", model, StringComparison.Ordinal);
        Assert.Contains("List<IFormFile>? Attachments", model, StringComparison.Ordinal);
        Assert.Contains("model.Attachments?.Where", controller, StringComparison.Ordinal);
        Assert.Contains("附件 <span class=\"text-muted\">（可选）</span>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("发送时间", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_validation_runs_before_disabling_the_send_button()
    {
        var page = ReadBundledFile("OutboundMailIndex.cshtml");
        const string unobtrusiveValidation = "window.jQuery(this).valid()";
        const string disableButton = "button.disabled = true";

        Assert.Contains(unobtrusiveValidation, page, StringComparison.Ordinal);
        Assert.True(
            page.IndexOf(unobtrusiveValidation, StringComparison.Ordinal) <
            page.IndexOf(disableButton, StringComparison.Ordinal));
    }

    [Fact]
    public void Dmg_build_uses_the_app_version_in_its_only_output_name()
    {
        var source = ReadBundledFile("build-dmg.sh");

        Assert.Contains("CFBundleShortVersionString", source, StringComparison.Ordinal);
        Assert.Contains("AppleSilicon-v$APP_VERSION.dmg", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppleSilicon.dmg\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_import_accepts_multiple_standard_csv_files_without_changing_outbound_tasks()
    {
        var page = ReadBundledFile("MailAccountsImportCsv.cshtml");
        var model = ReadBundledFile("BulkImportImapViewModel.cs");
        var controller = ReadBundledFile("MailAccountsController.cs");
        var outboundPage = ReadBundledFile("OutboundMailTasksIndex.cshtml");

        Assert.Contains("asp-for=\"AccountFiles\"", page, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv,text/csv\"", page, StringComparison.Ordinal);
        Assert.Contains("multiple required", page, StringComparison.Ordinal);
        Assert.Contains("List<IFormFile> AccountFiles", model, StringComparison.Ordinal);
        Assert.Contains("foreach (var file in accountFiles)", controller, StringComparison.Ordinal);
        Assert.Contains("ParseAccountImportFileAsync(file, model)", controller, StringComparison.Ordinal);
        Assert.Contains("不要求固定列数", page, StringComparison.Ordinal);
        Assert.Contains("extension.Equals(\".csv\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain(".txt,text/plain", page, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv,text/csv\"", outboundPage, StringComparison.Ordinal);
        Assert.DoesNotContain("multiple required", outboundPage, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_csv_import_stores_opaque_credentials_without_connecting_to_providers()
    {
        var page = ReadBundledFile("MailAccountsImportCsv.cshtml");
        var controller = ReadBundledFile("MailAccountsController.cs");
        var service = ReadBundledFile("CsvImportService.cs");

        Assert.Contains("不在导入时连接邮箱服务器", page, StringComparison.Ordinal);
        Assert.Contains("能识别到有效邮箱和非空授权码即导入成功", page, StringComparison.Ordinal);
        Assert.Contains("打开邮箱或点击刷新时才连接邮箱服务器", page, StringComparison.Ordinal);
        Assert.Contains("_csvImportService.QueueImport(job)", controller, StringComparison.Ordinal);
        Assert.Contains("new MailCredentialIntake(row.Email, row.Password, row.Domain, row.ClientId)", service, StringComparison.Ordinal);
        Assert.Contains("verifyCredential: false", service, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeAppPassword(row.Password)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_import_returns_a_background_job_before_database_intake_finishes()
    {
        var controller = ReadBundledFile("MailAccountsController.cs");
        var service = ReadBundledFile("CsvImportService.cs");
        var statusPage = ReadBundledFile("MailAccountsCsvImportStatus.cshtml");
        var importAction = controller[
            controller.IndexOf("public async Task<IActionResult> ImportCsv", StringComparison.Ordinal)
            ..controller.IndexOf("private async Task<AccountImportFileParseResult>", StringComparison.Ordinal)];
        Assert.Contains("_csvImportService.QueueImport(job)", importAction, StringComparison.Ordinal);
        Assert.Contains("RedirectToAction(nameof(CsvImportStatus)", importAction, StringComparison.Ordinal);
        Assert.DoesNotContain("await _mailCredentialIntake.UpsertAsync", importAction, StringComparison.Ordinal);
        Assert.Contains("verifyCredential: false", service, StringComparison.Ordinal);
        Assert.Contains("文件已识别，后台任务已受理", statusPage, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_list_loads_a_bounded_first_page_without_requiring_search()
    {
        var controller = ReadBundledFile("MailAccountsController.cs");
        var page = ReadBundledFile("MailAccountsIndex.cshtml");
        var indexAction = controller[
            controller.IndexOf("public async Task<IActionResult> Index(string? q", StringComparison.Ordinal)
            ..controller.IndexOf("// GET: MailAccounts/Details", StringComparison.Ordinal)];

        Assert.DoesNotContain("return View(Array.Empty<MailAccountViewModel>())", indexAction, StringComparison.Ordinal);
        Assert.Contains(".Skip((page - 1) * pageSize)", indexAction, StringComparison.Ordinal);
        Assert.Contains(".Take(pageSize)", indexAction, StringComparison.Ordinal);
        Assert.Contains("邮箱账号默认分页显示", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_and_compose_pages_do_not_materialize_every_owned_account()
    {
        var emailsController = ReadBundledFile("EmailsController.cs");
        var inboxAction = emailsController[
            emailsController.IndexOf("public async Task<IActionResult> Index(SearchViewModel model)", StringComparison.Ordinal)
            ..emailsController.IndexOf("// GET: Emails/Details", StringComparison.Ordinal)];
        var outboundController = ReadBundledFile("OutboundMailController.cs");

        Assert.DoesNotContain("GetUserMailAccountsAsync", inboxAction, StringComparison.Ordinal);
        Assert.Contains("a.UserMailAccounts.Any", inboxAction, StringComparison.Ordinal);
        Assert.Contains("allowedUserId: currentUserId.Value", inboxAction, StringComparison.Ordinal);
        Assert.Contains("MaxSendingAccountOptions = 50", outboundController, StringComparison.Ordinal);
        Assert.Contains(".Take(candidateLimit)", outboundController, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_mail_data_keeps_identity_and_mailbox_tables_out_of_the_delete_set()
    {
        var controller = ReadBundledFile("LocalMaintenanceController.cs");
        var action = controller[
            controller.IndexOf("public async Task<IActionResult> ClearMailData", StringComparison.Ordinal)
            ..controller.IndexOf("public IActionResult FactoryReset", StringComparison.Ordinal)];

        Assert.DoesNotContain("Users.ExecuteDeleteAsync", action, StringComparison.Ordinal);
        Assert.DoesNotContain("MailAccounts.ExecuteDeleteAsync", action, StringComparison.Ordinal);
        Assert.DoesNotContain("UserMailAccounts.ExecuteDeleteAsync", action, StringComparison.Ordinal);
        Assert.Contains("_csvImportService.HasActiveJobs()", action, StringComparison.Ordinal);
        Assert.True(
            action.IndexOf("try", StringComparison.Ordinal) <
            action.IndexOf("BeginTransactionAsync", StringComparison.Ordinal));
        Assert.Contains("await using var transaction", action, StringComparison.Ordinal);
        Assert.Contains("登录信息没有被删除", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_list_refresh_always_exposes_progress_and_a_terminal_result()
    {
        var page = ReadBundledFile("MailAccountsIndex.cshtml");

        Assert.Contains("account-sync-button", page, StringComparison.Ordinal);
        Assert.Contains("button.classList.add('is-syncing')", page, StringComparison.Ordinal);
        Assert.Contains("button.classList.remove('is-syncing')", page, StringComparison.Ordinal);
        Assert.Contains("同步完成，未检测到新邮件。", page, StringComparison.Ordinal);
        Assert.Contains("同步完成，新增", page, StringComparison.Ordinal);
        Assert.Contains("同步失败", page, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", page, StringComparison.Ordinal);
        Assert.Contains("if (requestedAt)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_test_runner_stops_stale_servers_from_test_and_packaged_bundles()
    {
        var source = ReadBundledFile("build_and_run.sh");

        Assert.Contains("temporary_server_assembly", source, StringComparison.Ordinal);
        Assert.Contains("packaged_server_assembly", source, StringComparison.Ordinal);
        Assert.Contains("$ROOT_DIR/local-app/build/$APP_NAME.app", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Gmail_app_password_is_normalized_only_when_authentication_is_attempted()
    {
        var controller = ReadBundledFile("MailAccountsController.cs");
        var service = ReadBundledFile("CsvImportService.cs");
        var createAction = controller[
            controller.IndexOf("public async Task<IActionResult> Create(CreateMailAccountViewModel model)", StringComparison.Ordinal)
            ..controller.IndexOf("private async Task<IActionResult> CreateM365TenantAccountsAsync", StringComparison.Ordinal)];
        var editAction = controller[
            controller.IndexOf("public async Task<IActionResult> Edit(int id, MailAccountViewModel model", StringComparison.Ordinal)
            ..controller.IndexOf("// GET: MailAccounts/Delete/5", StringComparison.Ordinal)];
        var importAction = controller[
            controller.IndexOf("public async Task<IActionResult> ImportCsv", StringComparison.Ordinal)
            ..controller.IndexOf("private async Task<AccountImportFileParseResult>", StringComparison.Ordinal)];

        Assert.Contains("mailProviderModule.NormalizeAppPassword(model.Password)", createAction, StringComparison.Ordinal);
        Assert.Contains("mailProviderModule.NormalizeAppPassword(model.Password)", editAction, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeAppPassword(row.Password)", importAction, StringComparison.Ordinal);
        Assert.Contains("new MailCredentialIntake(row.Email, row.Password, row.Domain, row.ClientId)", service, StringComparison.Ordinal);

        var createPage = ReadBundledFile("MailAccountsCreate.cshtml");
        var editPage = ReadBundledFile("MailAccountsEdit.cshtml");
        var importPage = ReadBundledFile("MailAccountsImportCsv.cshtml");
        Assert.Contains("Google 应用专用密码，不是 Google 登录密码", createPage, StringComparison.Ordinal);
        Assert.Contains("xxxx xxxx xxxx xxxx", createPage, StringComparison.Ordinal);
        Assert.Contains("Google 应用专用密码，不是 Google 登录密码", editPage, StringComparison.Ordinal);
        Assert.Contains("16 位授权码可带分组空格", importPage, StringComparison.Ordinal);
        Assert.Contains("导入时会自动清理", importPage, StringComparison.Ordinal);
    }

    [Fact]
    public void Mac_launcher_forwards_an_active_GW_local_proxy_to_the_mail_server()
    {
        var launcher = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("Application Support/gw/vortex.json", launcher, StringComparison.Ordinal);
        Assert.Contains("proxy_port", launcher, StringComparison.Ordinal);
        Assert.Contains("MailProxy__Enabled", launcher, StringComparison.Ordinal);
        Assert.Contains("MailProxy__Type", launcher, StringComparison.Ordinal);
        Assert.Contains("MailProxy__Host", launcher, StringComparison.Ordinal);
        Assert.Contains("MailProxy__Port", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_tasks_offer_immediate_and_csv_scheduled_modes()
    {
        var page = ReadBundledFile("OutboundMailTasksIndex.cshtml");

        Assert.Contains("点击即发（推荐）", page, StringComparison.Ordinal);
        Assert.Contains("不检查 CSV 的时间列", page, StringComparison.Ordinal);
        Assert.Contains("定时控制", page, StringComparison.Ordinal);
        Assert.Contains("每一行都要填写有效时间", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteAccountConfirmation_uses_a_plain_form_without_modal_backdrops()
    {
        var source = ReadBundledFile("Delete.cshtml");

        Assert.Contains("type=\"submit\" class=\"btn btn-danger\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-bs-toggle=\"modal\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deleteConfirmationModal", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountList_uses_one_compact_delete_modal_and_a_previewed_whitelist_flow()
    {
        var source = ReadBundledFile("MailAccountsIndex.cshtml");

        Assert.Contains("data-bs-target=\"#deleteAccountModal\"", source, StringComparison.Ordinal);
        Assert.Contains("删除后该邮箱账号及", source, StringComparison.Ordinal);
        Assert.Contains("data-bs-target=\"#whitelistDeletionModal\"", source, StringComparison.Ordinal);
        Assert.Contains("WhitelistDeletionPreview", source, StringComparison.Ordinal);
        Assert.Contains("PreviewFingerprint", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyArchiveGuard_does_not_block_account_deletion()
    {
        var source = ReadBundledFile("StrictReadOnlyArchiveFilter.cs");

        Assert.Contains("controller == \"EmailsController\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("controller == \"MailAccountsController\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWrapper_ImplementsFileSelectionForWebUploads()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("WKUIDelegate", source, StringComparison.Ordinal);
        Assert.Contains("webView.uiDelegate = self", source, StringComparison.Ordinal);
        Assert.Contains("runOpenPanelWith parameters", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWrapper_downloads_attachment_responses_instead_of_rendering_them()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("WKDownloadDelegate", source, StringComparison.Ordinal);
        Assert.Contains("decidePolicyFor navigationResponse", source, StringComparison.Ordinal);
        Assert.Contains(".download", source, StringComparison.Ordinal);
        Assert.Contains("didBecome download: WKDownload", source, StringComparison.Ordinal);
        Assert.Contains("decideDestinationUsing response", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWrapper_implements_web_confirmation_dialogs()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("runJavaScriptConfirmPanelWithMessage message", source, StringComparison.Ordinal);
        Assert.Contains("completionHandler(response == .alertFirstButtonReturn)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWrapper_routes_command_c_and_command_a_to_the_current_web_selection()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("configureMainMenu()", source, StringComparison.Ordinal);
        Assert.Contains("#selector(NSText.copy(_:))", source, StringComparison.Ordinal);
        Assert.Contains("#selector(NSText.selectAll(_:))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWrapper_routes_command_v_to_the_focused_web_input()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("#selector(NSText.paste(_:))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppContent_explicitly_allows_text_selection()
    {
        var source = ReadBundledFile("site.css");

        Assert.Contains("-webkit-user-select: text !important;", source, StringComparison.Ordinal);
        Assert.Contains("user-select: text !important;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalFactoryReset_clears_database_credentials_and_native_webview_data()
    {
        var source = ReadBundledFile("KouziMailAssistant.swift");

        Assert.Contains("webKitDataDirectory", source, StringComparison.Ordinal);
        Assert.Contains("httpStorageDirectory", source, StringComparison.Ordinal);
        Assert.Contains("cacheDirectory", source, StringComparison.Ordinal);
        Assert.Contains("let directories = [dataDirectory, webKitDataDirectory, httpStorageDirectory, cacheDirectory]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalMaintenance_exposes_a_single_action_to_clear_all_machine_data()
    {
        var source = ReadBundledFile("LocalMaintenanceIndex.cshtml");

        Assert.Contains("清空本机所有数据", source, StringComparison.Ordinal);
        Assert.Contains("登录状态和本机密钥", source, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"confirmation\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MailboxRefresh_stays_on_the_current_mailbox_and_uses_a_scanning_status()
    {
        var page = ReadBundledFile("EmailsIndex.cshtml");
        var controller = ReadBundledFile("MailAccountsController.cs");

        Assert.Contains("mailbox-refresh-form", page, StringComparison.Ordinal);
        Assert.Contains("X-Requested-With", page, StringComparison.Ordinal);
        Assert.Contains("mailbox-sync-scan", page, StringComparison.Ordinal);
        Assert.Contains("X-Requested-With", controller, StringComparison.Ordinal);
        Assert.Contains("state = queueStatus.State.ToString()", controller, StringComparison.Ordinal);
        Assert.Contains("queueStatus.State == MailSyncQueueState.Running", controller, StringComparison.Ordinal);
        Assert.Contains("requestedAt.ToString(\"O\")", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailbox_received_dates_are_rendered_through_the_fixed_Beijing_converter()
    {
        var page = ReadBundledFile("EmailsIndex.cshtml");

        Assert.Contains("data-utc-time=", page, StringComparison.Ordinal);
        Assert.Contains("@email.ReceivedDate.ToString(\"MM-dd HH:mm\")", page, StringComparison.Ordinal);
        Assert.Contains("@selectedEmail.ReceivedDate.ToString(\"yyyy-MM-dd HH:mm\")", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailbox_status_checks_never_enqueue_another_sync()
    {
        var emailsController = ReadBundledFile("EmailsController.cs");
        var accountsController = ReadBundledFile("MailAccountsController.cs");
        var watchSync = accountsController[
            accountsController.IndexOf("public async Task<IActionResult> WatchSync", StringComparison.Ordinal)
            ..accountsController.IndexOf("[HttpGet]", accountsController.IndexOf("public async Task<IActionResult> WatchSync", StringComparison.Ordinal), StringComparison.Ordinal)];

        Assert.DoesNotContain(
            "_onDemandSyncQueue.Enqueue(selectedAccount.Id",
            emailsController,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_onDemandSyncQueue.Enqueue(",
            watchSync,
            StringComparison.Ordinal);
        Assert.Contains(
            "_onDemandSyncQueue.GetStatus(id)",
            watchSync,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Local_search_routes_around_postgresql_only_sql()
    {
        var source = ReadBundledFile("EmailCoreService.cs");

        Assert.Contains(
            "_context.Database.IsNpgsql()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Imap_sync_probes_recent_inbox_uids_and_prefers_internal_delivery_dates()
    {
        var source = ReadBundledFile("ImapMailSyncService.cs");

        Assert.Contains("IncludeRecentInboxCandidatesAsync", source, StringComparison.Ordinal);
        Assert.Contains(
            "summary.InternalDate?.UtcDateTime\n                        ?? summary.Envelope?.Date?.UtcDateTime",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_reader_uses_the_sanitized_HTML_route_instead_of_forcing_plain_text()
    {
        var page = ReadBundledFile("EmailsIndex.cshtml");
        Assert.Contains("<iframe", page, StringComparison.Ordinal);
        Assert.Contains("Url.Action(\"RawContent\", new { id = selectedEmail.Id })", page, StringComparison.Ordinal);
        Assert.Contains("sandbox=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<pre>@selectedEmail.Body</pre>", page, StringComparison.Ordinal);
    }

    private static string ReadBundledFile(string fileName)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));
    }
}
