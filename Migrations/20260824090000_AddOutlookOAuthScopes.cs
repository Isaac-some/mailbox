using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260824090000_AddOutlookOAuthScopes")]
public partial class AddOutlookOAuthScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OAuthGrantedScopes",
            schema: "mail_archiver",
            table: "MailAccounts",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OAuthGrantedScopes",
            schema: "mail_archiver",
            table: "MailAccounts");
    }
}
