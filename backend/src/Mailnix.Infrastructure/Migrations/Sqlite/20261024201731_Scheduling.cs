using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Scheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchFilter",
                table: "Runs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastScheduledAt",
                table: "Rules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastPurgeAt",
                table: "MailAccounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrashObservations",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Uid = table.Column<uint>(type: "INTEGER", nullable: false),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrashObservations", x => new { x.AccountId, x.Folder, x.Uid });
                    table.ForeignKey(
                        name: "FK_TrashObservations_MailAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrashObservations");

            migrationBuilder.DropColumn(
                name: "SearchFilter",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "LastScheduledAt",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "LastPurgeAt",
                table: "MailAccounts");
        }
    }
}
