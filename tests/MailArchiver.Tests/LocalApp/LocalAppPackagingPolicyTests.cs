using System.Text.Json;

namespace MailArchiver.Tests.LocalApp;

public class LocalAppPackagingPolicyTests
{
    [Fact]
    public void LocalAppConfiguration_EnablesAutomaticMailSync()
    {
        using var document = JsonDocument.Parse(ReadBundledFile("appsettings.Local.json"));

        Assert.True(document.RootElement
            .GetProperty("MailSync")
            .GetProperty("Enabled")
            .GetBoolean());
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

        Assert.Contains("<string>1.0.13</string>", source, StringComparison.Ordinal);
        Assert.Contains("<string>14</string>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRelease_matches_the_macOS_feature_version()
    {
        var source = ReadBundledFile("KouziMailAssistant.Windows.csproj");

        Assert.Contains("<Version>1.0.13</Version>", source, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>1.0.13.0</FileVersion>", source, StringComparison.Ordinal);
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
    public void Dmg_build_uses_the_app_version_in_its_only_output_name()
    {
        var source = ReadBundledFile("build-dmg.sh");

        Assert.Contains("CFBundleShortVersionString", source, StringComparison.Ordinal);
        Assert.Contains("AppleSilicon-v$APP_VERSION.dmg", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppleSilicon.dmg\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_import_accepts_multiple_txt_and_csv_files_without_changing_outbound_tasks()
    {
        var page = ReadBundledFile("MailAccountsImportCsv.cshtml");
        var model = ReadBundledFile("BulkImportImapViewModel.cs");
        var controller = ReadBundledFile("MailAccountsController.cs");
        var outboundPage = ReadBundledFile("OutboundMailTasksIndex.cshtml");

        Assert.Contains("asp-for=\"AccountFiles\"", page, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv,.txt,text/csv,text/plain\"", page, StringComparison.Ordinal);
        Assert.Contains("multiple required", page, StringComparison.Ordinal);
        Assert.Contains("List<IFormFile> AccountFiles", model, StringComparison.Ordinal);
        Assert.Contains("foreach (var file in accountFiles)", controller, StringComparison.Ordinal);
        Assert.Contains("ParseAccountImportFileAsync(file, model)", controller, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv,text/csv\"", outboundPage, StringComparison.Ordinal);
        Assert.DoesNotContain("multiple required", outboundPage, StringComparison.Ordinal);
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
        Assert.Contains("return Json(new { state = queueStatus.State.ToString() })", controller, StringComparison.Ordinal);
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

    private static string ReadBundledFile(string fileName)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));
    }
}
