using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260920090000_AddSyncFailureRecords")]
public sealed class AddSyncFailureRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SyncFailureRecords",
            schema: "mail_archiver",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                JobId = table.Column<string>(type: "text", nullable: false),
                MailAccountId = table.Column<int>(type: "integer", nullable: false),
                Operation = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: false),
                FailureStage = table.Column<string>(type: "text", nullable: false),
                ExceptionCategory = table.Column<string>(type: "text", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SyncFailureRecords", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_SyncFailureRecords_OccurredAtUtc",
            schema: "mail_archiver",
            table: "SyncFailureRecords",
            column: "OccurredAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "SyncFailureRecords", schema: "mail_archiver");
}
