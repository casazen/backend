using System;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// BK-03 (A3-02, A5-16, A8-23): one source for the tourist tax. Drops <c>TaxRates</c> (never written, it made every
    /// booking tax 0) and extends <c>TouristTaxRates</c> with ISTAT code, accommodation category, season, percentage
    /// with cap and reduced age band; then sets the ISTAT code of the CO-03 rows and loads the rows the model now
    /// represents (<see cref="TouristTaxRateSeed.BuildCategoryAndSeasonRates"/>). Runbook:
    /// <c>docs/runbooks/tourist-tax-rates.md</c>.
    /// </summary>
    public partial class UnifyTouristTaxOnTouristTaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxRates");

            migrationBuilder.AddColumn<string>(
                name: "AccommodationCategory",
                table: "TouristTaxRates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalculationMethod",
                table: "TouristTaxRates",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "PerPersonPerNight");

            migrationBuilder.AddColumn<decimal>(
                name: "CapPerPersonPerNight",
                table: "TouristTaxRates",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IstatCode",
                table: "TouristTaxRates",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PercentOfNightlyPrice",
                table: "TouristTaxRates",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReducedRateMaxAge",
                table: "TouristTaxRates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReducedRatePerPersonPerNight",
                table: "TouristTaxRates",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeasonEnd",
                table: "TouristTaxRates",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeasonStart",
                table: "TouristTaxRates",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TouristTaxRates_IstatCode",
                table: "TouristTaxRates",
                column: "IstatCode");

            TouristTaxRateSeed.InsertCategoryAndSeasonRates(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TouristTaxRateSeed.DeleteCategoryAndSeasonRates(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_TouristTaxRates_IstatCode",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "AccommodationCategory",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "CalculationMethod",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "CapPerPersonPerNight",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "IstatCode",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "PercentOfNightlyPrice",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "ReducedRateMaxAge",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "ReducedRatePerPersonPerNight",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "SeasonEnd",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "SeasonStart",
                table: "TouristTaxRates");

            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxNights = table.Column<int>(type: "integer", nullable: true),
                    RatePerNight = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                });
        }
    }
}
