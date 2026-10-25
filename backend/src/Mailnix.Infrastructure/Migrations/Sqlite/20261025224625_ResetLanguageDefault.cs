using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mailnix.Infrastructure.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ResetLanguageDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "en" was the default, never a choice (there was no setting yet): let the browser's language apply.
            // A migration of its own: on SQLite the previous one rebuilds the table to make the column nullable.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"Language\" = NULL WHERE \"Language\" = 'en';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
