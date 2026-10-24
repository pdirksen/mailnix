using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Sqlite
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
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    LastProcessedUid = table.Column<uint>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Limit = table.Column<int>(type: "INTEGER", nullable: true),
                    RuleIds = table.Column<string>(type: "TEXT", nullable: true),
                    UndoRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UndoRuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UndoCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingPlanJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MessagesExamined = table.Column<int>(type: "INTEGER", nullable: false),
                    MessagesMatched = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedActions = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutedActions = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedActions = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UndoneByRunId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RuleName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Keyword = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceFolder = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourceUid = table.Column<uint>(type: "INTEGER", nullable: false),
                    SourceUidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TargetUid = table.Column<uint>(type: "INTEGER", nullable: true),
                    TargetUidValidity = table.Column<uint>(type: "INTEGER", nullable: true),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    From = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PreviousFlags = table.Column<string>(type: "TEXT", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UndoneAt = table.Column<long>(type: "INTEGER", nullable: true)
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
