using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260917120000_AddMailboxFolderCategories")]
public partial class AddMailboxFolderCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FolderCategory",
            schema: "mail_archiver",
            table: "ArchivedEmails",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Other");

        migrationBuilder.AddColumn<int>(
            name: "ExpandedMailboxCategories",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "MailboxSyncLookbackDays",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 7);

        var archivedEmailsTable = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            ? "\"ArchivedEmails\""
            : "mail_archiver.\"ArchivedEmails\"";
        migrationBuilder.Sql($$"""
            UPDATE {{archivedEmailsTable}}
            SET "FolderCategory" = CASE
                WHEN LOWER("FolderName") IN ('inbox', '收件箱', 'posteingang') THEN 'Inbox'
                WHEN LOWER("FolderName") IN ('bulk', 'junk', 'junk email', 'junk e-mail', 'spam', 'spamverdacht', '垃圾邮件') THEN 'Junk'
                WHEN LOWER("FolderName") LIKE '%sent%' OR LOWER("FolderName") LIKE '%已发送%' OR LOWER("FolderName") LIKE '%gesendet%' THEN 'Sent'
                WHEN LOWER("FolderName") LIKE '%trash%' OR LOWER("FolderName") LIKE '%deleted%' OR LOWER("FolderName") LIKE '%已删除%'
                    OR LOWER("FolderName") IN ('gelöscht', 'geloescht', 'geloscht') THEN 'Trash'
                WHEN LOWER("FolderName") LIKE '%draft%' OR LOWER("FolderName") LIKE '%草稿%' THEN 'Drafts'
                WHEN LOWER("FolderName") LIKE '%archive%' OR LOWER("FolderName") LIKE '%归档%' THEN 'Archive'
                WHEN "IsOutgoing" THEN 'Sent'
                ELSE 'Other'
            END
            """);

        migrationBuilder.CreateIndex(
            name: "IX_ArchivedEmails_Account_Category_ReceivedDate",
            schema: "mail_archiver",
            table: "ArchivedEmails",
            columns: new[] { "MailAccountId", "FolderCategory", "ReceivedDate" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ArchivedEmails_Account_Category_ReceivedDate",
            schema: "mail_archiver",
            table: "ArchivedEmails");
        migrationBuilder.DropColumn(name: "FolderCategory", schema: "mail_archiver", table: "ArchivedEmails");
        migrationBuilder.DropColumn(name: "ExpandedMailboxCategories", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "MailboxSyncLookbackDays", schema: "mail_archiver", table: "MailAccounts");
    }
}
