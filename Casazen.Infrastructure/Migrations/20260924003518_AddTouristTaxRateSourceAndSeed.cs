using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-03 (A5-06): source URL and verification level on <c>TouristTaxRates</c>, then the rates of the pilot comuni
    /// that the model represents, from the RS-7 research (<see cref="TouristTaxRateSeed"/>). Runbook:
    /// <c>docs/runbooks/tourist-tax-rates.md</c>.
    /// </summary>
    public partial class AddTouristTaxRateSourceAndSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "TouristTaxRates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationLevel",
                table: "TouristTaxRates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            TouristTaxRateSeed.Insert(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TouristTaxRateSeed.Delete(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "VerificationLevel",
                table: "TouristTaxRates");
        }
    }
}
