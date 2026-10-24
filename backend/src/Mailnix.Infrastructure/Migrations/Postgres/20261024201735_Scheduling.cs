using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Postgres
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
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScheduledAt",
                table: "Rules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPurgeAt",
                table: "MailAccounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrashObservations",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folder = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
