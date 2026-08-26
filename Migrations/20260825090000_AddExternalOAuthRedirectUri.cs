using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260825090000_AddExternalOAuthRedirectUri")]
public partial class AddExternalOAuthRedirectUri : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OAuthRedirectUri",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OAuthRedirectUri",
            schema: "mail_archiver",
            table: "MailAccounts");
    }
}
