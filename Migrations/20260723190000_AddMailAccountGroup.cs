using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

public partial class AddMailAccountGroup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            ALTER TABLE mail_archiver.""MailAccounts""
            ADD COLUMN IF NOT EXISTS ""GroupName"" text NOT NULL DEFAULT '';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            ALTER TABLE mail_archiver.""MailAccounts""
            DROP COLUMN IF EXISTS ""GroupName"";");
    }
}
