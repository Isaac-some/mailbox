using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260724193000_AddRegistrationCodes")]
public partial class AddRegistrationCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RegistrationCodes",
            schema: "mail_archiver",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CodePrefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                UsedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                UsedByUserId = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RegistrationCodes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RegistrationCodes_CodeHash",
            schema: "mail_archiver",
            table: "RegistrationCodes",
            column: "CodeHash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RegistrationCodes",
            schema: "mail_archiver");
    }
}
