using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyCountryCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add Country column with default value "IT" (ADR-001-R7)
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Properties",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "IT");

            // Add Currency column with default value "EUR" (ADR-001-R7)
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Properties",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EUR");

            // Backfill existing properties: all to IT/EUR (ADR-001-R8)
            // The default value already handles this, but explicit UPDATE for clarity
            migrationBuilder.Sql(@"
                UPDATE ""Properties""
                SET ""Country"" = 'IT', ""Currency"" = 'EUR'
                WHERE ""Country"" = '' OR ""Currency"" = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Country",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Properties");
        }
    }
}
