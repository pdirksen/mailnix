using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Runs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FolderStates",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    LastProcessedUid = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderStates", x => new { x.AccountId, x.Folder });
                    table.ForeignKey(
                        name: "FK_FolderStates_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Folder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Limit = table.Column<int>(type: "integer", nullable: true),
                    RuleIds = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    UndoRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    UndoRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    UndoCount = table.Column<int>(type: "integer", nullable: true),
                    PendingPlanJson = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessagesExamined = table.Column<int>(type: "integer", nullable: false),
                    MessagesMatched = table.Column<int>(type: "integer", nullable: false),
                    PlannedActions = table.Column<int>(type: "integer", nullable: false),
                    ExecutedActions = table.Column<int>(type: "integer", nullable: false),
                    FailedActions = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UndoneByRunId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceFolder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceUid = table.Column<long>(type: "bigint", nullable: false),
                    SourceUidValidity = table.Column<long>(type: "bigint", nullable: false),
                    TargetFolder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TargetUid = table.Column<long>(type: "bigint", nullable: true),
                    TargetUidValidity = table.Column<long>(type: "bigint", nullable: true),
                    MessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    From = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PreviousFlags = table.Column<List<string>>(type: "text[]", nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UndoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionLog_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLog_AccountId_TargetFolder_TargetUid",
                table: "ActionLog",
                columns: new[] { "AccountId", "TargetFolder", "TargetUid" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLog_RuleId_CreatedAt",
                table: "ActionLog",
                columns: new[] { "RuleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLog_RunId",
                table: "ActionLog",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_AccountId_State",
                table: "Runs",
                columns: new[] { "AccountId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_UserId_CreatedAt",
                table: "Runs",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionLog");

            migrationBuilder.DropTable(
                name: "FolderStates");

            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
