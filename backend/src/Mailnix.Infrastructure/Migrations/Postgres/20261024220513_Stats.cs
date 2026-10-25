using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Stats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "UndoActionIds",
                table: "Runs",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FolderSnapshots",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Messages = table.Column<int>(type: "integer", nullable: false),
                    Unread = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderSnapshots", x => new { x.AccountId, x.Folder, x.Date });
                    table.ForeignKey(
                        name: "FK_FolderSnapshots_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityDaily",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Processed = table.Column<int>(type: "integer", nullable: false),
                    SpfFailed = table.Column<int>(type: "integer", nullable: false),
                    DkimFailed = table.Column<int>(type: "integer", nullable: false),
                    DmarcFailed = table.Column<int>(type: "integer", nullable: false),
                    Spoofing = table.Column<int>(type: "integer", nullable: false),
                    HighSpam = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityDaily", x => new { x.AccountId, x.Date });
                    table.ForeignKey(
                        name: "FK_SecurityDaily_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SenderDaily",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Processed = table.Column<int>(type: "integer", nullable: false),
                    Actioned = table.Column<int>(type: "integer", nullable: false),
                    Bulk = table.Column<int>(type: "integer", nullable: false),
                    AuthFailed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderDaily", x => new { x.AccountId, x.Date, x.Domain });
                    table.ForeignKey(
                        name: "FK_SenderDaily_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatsDaily",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatsDaily", x => new { x.AccountId, x.Date, x.RuleId, x.Metric });
                    table.ForeignKey(
                        name: "FK_StatsDaily_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderSnapshots_UserId_Date",
                table: "FolderSnapshots",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDaily_UserId_Date",
                table: "SecurityDaily",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SenderDaily_UserId_Date",
                table: "SenderDaily",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_StatsDaily_UserId_Date",
                table: "StatsDaily",
                columns: new[] { "UserId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderSnapshots");

            migrationBuilder.DropTable(
                name: "SecurityDaily");

            migrationBuilder.DropTable(
                name: "SenderDaily");

            migrationBuilder.DropTable(
                name: "StatsDaily");

            migrationBuilder.DropColumn(
                name: "UndoActionIds",
                table: "Runs");
        }
    }
}
