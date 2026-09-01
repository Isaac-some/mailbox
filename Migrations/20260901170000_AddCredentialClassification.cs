using Microsoft.EntityFrameworkCore.Migrations;
using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260901170000_AddCredentialClassification")]
public partial class AddCredentialClassification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SmtpServer",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SmtpPort",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SmtpUseSSL",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EndpointDiscoveryStatus",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "EndpointDiscoveryLastCheckedAt",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "timestamp without time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CredentialKind",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Unknown");

        migrationBuilder.AddColumn<string>(
            name: "CredentialScope",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Unknown");

        migrationBuilder.AddColumn<string>(
            name: "CredentialDetectionStatus",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CredentialLastCheckedAt",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "timestamp without time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SmtpServer", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "SmtpPort", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "SmtpUseSSL", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "EndpointDiscoveryStatus", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "EndpointDiscoveryLastCheckedAt", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "CredentialKind", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "CredentialScope", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "CredentialDetectionStatus", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "CredentialLastCheckedAt", schema: "mail_archiver", table: "MailAccounts");
    }
}
