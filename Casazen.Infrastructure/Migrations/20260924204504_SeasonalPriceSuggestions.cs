using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PC-15 (D4, A2-14, A2-34): "Suggerimenti stagionali" instead of the fake "AI pricing".
    /// <list type="bullet">
    /// <item>The host's explicit rules on <c>PricingAdapterConfigs</c> (season months, multipliers); the existing configurations
    /// get the documented example rule (June-August x1.30, November-February x0.80, national holidays x1.50) that the host
    /// can edit.</item>
    /// <item><c>NextScheduledRunAt</c> is dropped: the next computation is derived from <c>LastAdaptedAt</c> by Rome dates.</item>
    /// <item><c>SeasonalPriceSuggestions</c>: one row per property and stay date, regenerated in place.</item>
    /// <item>The rows the old job invented in <c>PricingHistories</c> (base 100 EUR, confidence 0.85, 91 rows a day) are
    /// <b>deleted</b>, not marked: no real price was ever computed or applied from them and nothing reads them any more.
    /// The rows of the OTA batch push (in freeze) are kept. Down cannot bring the deleted rows back.</item>
    /// </list>
    /// </summary>
    public partial class SeasonalPriceSuggestions : Migration
    {
        /// <summary>
        /// Deletes the invented history of the old <c>DynamicPricingJob</c>: always base 100, confidence 0.85 and its fixed
        /// reason. Public so that a PostgreSQL test runs exactly this statement.
        /// </summary>
        public const string DeleteInventedHistorySql = """
            DELETE FROM "PricingHistories"
            WHERE "PreviousPrice" = 100
              AND "AiConfidence" = 0.85
              AND "ChangeReason" LIKE 'Dynamic pricing adaptation (multiplier:%';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextScheduledRunAt",
                table: "PricingAdapterConfigs");

            migrationBuilder.AddColumn<List<int>>(
                name: "HighSeasonMonths",
                table: "PricingAdapterConfigs",
                type: "integer[]",
                nullable: false,
                defaultValue: new List<int> { 6, 7, 8 });

            migrationBuilder.AddColumn<decimal>(
                name: "HighSeasonMultiplier",
                table: "PricingAdapterConfigs",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 1.30m);

            migrationBuilder.AddColumn<decimal>(
                name: "HolidayMultiplier",
                table: "PricingAdapterConfigs",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 1.50m);

            migrationBuilder.AddColumn<List<int>>(
                name: "LowSeasonMonths",
                table: "PricingAdapterConfigs",
                type: "integer[]",
                nullable: false,
                defaultValue: new List<int> { 11, 12, 1, 2 });

            migrationBuilder.AddColumn<decimal>(
                name: "LowSeasonMultiplier",
                table: "PricingAdapterConfigs",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0.80m);

            migrationBuilder.CreateTable(
                name: "SeasonalPriceSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    StayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BasePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SuggestedPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Multiplier = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    Rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Holiday = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonalPriceSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonalPriceSuggestions_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeasonalPriceSuggestions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalPriceSuggestions_OrgId",
                table: "SeasonalPriceSuggestions",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalPriceSuggestions_PropertyId_StayDate",
                table: "SeasonalPriceSuggestions",
                columns: new[] { "PropertyId", "StayDate" },
                unique: true);

            migrationBuilder.Sql(DeleteInventedHistorySql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeasonalPriceSuggestions");

            migrationBuilder.DropColumn(
                name: "HighSeasonMonths",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "HighSeasonMultiplier",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "HolidayMultiplier",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "LowSeasonMonths",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "LowSeasonMultiplier",
                table: "PricingAdapterConfigs");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextScheduledRunAt",
                table: "PricingAdapterConfigs",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
