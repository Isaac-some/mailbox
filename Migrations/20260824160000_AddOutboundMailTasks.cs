using MailArchiver.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260824160000_AddOutboundMailTasks")]
public partial class AddOutboundMailTasks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OutboundMailTasks",
            schema: "mail_archiver",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboundMailTasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_OutboundMailTasks_Users_CreatedByUserId",
                    column: x => x.CreatedByUserId,
                    principalSchema: "mail_archiver",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OutboundMailTaskItems",
            schema: "mail_archiver",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OutboundMailTaskId = table.Column<int>(type: "integer", nullable: false),
                MailAccountId = table.Column<int>(type: "integer", nullable: false),
                CsvRowNumber = table.Column<int>(type: "integer", nullable: false),
                ScheduledAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                Recipient = table.Column<string>(type: "text", nullable: false),
                Subject = table.Column<string>(type: "text", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                MessageId = table.Column<string>(type: "text", nullable: true),
                SentCopySaved = table.Column<bool>(type: "boolean", nullable: true),
                ErrorMessage = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboundMailTaskItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_OutboundMailTaskItems_MailAccounts_MailAccountId",
                    column: x => x.MailAccountId,
                    principalSchema: "mail_archiver",
                    principalTable: "MailAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OutboundMailTaskItems_OutboundMailTasks_OutboundMailTaskId",
                    column: x => x.OutboundMailTaskId,
                    principalSchema: "mail_archiver",
                    principalTable: "OutboundMailTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OutboundMailTasks_CreatedByUserId",
            schema: "mail_archiver",
            table: "OutboundMailTasks",
            column: "CreatedByUserId");
        migrationBuilder.CreateIndex(
            name: "IX_OutboundMailTaskItems_MailAccountId",
            schema: "mail_archiver",
            table: "OutboundMailTaskItems",
            column: "MailAccountId");
        migrationBuilder.CreateIndex(
            name: "IX_OutboundMailTaskItems_OutboundMailTaskId",
            schema: "mail_archiver",
            table: "OutboundMailTaskItems",
            column: "OutboundMailTaskId");
        migrationBuilder.CreateIndex(
            name: "IX_OutboundMailTaskItems_Status_ScheduledAtUtc",
            schema: "mail_archiver",
            table: "OutboundMailTaskItems",
            columns: new[] { "Status", "ScheduledAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OutboundMailTaskItems", schema: "mail_archiver");
        migrationBuilder.DropTable(name: "OutboundMailTasks", schema: "mail_archiver");
    }
}
