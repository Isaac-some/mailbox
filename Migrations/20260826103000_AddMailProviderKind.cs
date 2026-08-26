using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260826103000_AddMailProviderKind")]
public partial class AddMailProviderKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MailProviderKind",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE mail_archiver."MailAccounts"
            SET "MailProviderKind" = CASE
                WHEN "Provider" = 'MSA' THEN 'Outlook'
                WHEN "Provider" = 'IMAP' AND (LOWER("EmailAddress") LIKE '%@gmail.com' OR LOWER("EmailAddress") LIKE '%@googlemail.com') THEN 'Gmail'
                WHEN "Provider" = 'IMAP' AND LOWER("EmailAddress") LIKE '%@yahoo.%' THEN 'Yahoo'
                WHEN "Provider" = 'IMAP' AND (LOWER("EmailAddress") LIKE '%@gmx.com' OR LOWER("EmailAddress") LIKE '%@gmx.net' OR LOWER("EmailAddress") LIKE '%@gmx.de') THEN 'Gmx'
                ELSE "MailProviderKind"
            END
            WHERE "MailProviderKind" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MailProviderKind",
            schema: "mail_archiver",
            table: "MailAccounts");
    }
}
