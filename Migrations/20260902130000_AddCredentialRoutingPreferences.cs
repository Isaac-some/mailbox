using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260902130000_AddCredentialRoutingPreferences")]
public partial class AddCredentialRoutingPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ImportedDomain",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PreferredIncomingAuth",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Unknown");

        migrationBuilder.AddColumn<string>(
            name: "PreferredOutgoingAuth",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Unknown");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImportedDomain", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "PreferredIncomingAuth", schema: "mail_archiver", table: "MailAccounts");
        migrationBuilder.DropColumn(name: "PreferredOutgoingAuth", schema: "mail_archiver", table: "MailAccounts");
    }
}
