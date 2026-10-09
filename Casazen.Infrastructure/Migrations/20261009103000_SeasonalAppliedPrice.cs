using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

/// <summary>Host-confirmed seasonal nightly prices (PO 2026-10-08). Proposals stay unused until applied.</summary>
public class SeasonalAppliedPrice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "AppliedPrice",
            table: "SeasonalPriceSuggestions",
            type: "numeric(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AppliedAt",
            table: "SeasonalPriceSuggestions",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AppliedPrice", table: "SeasonalPriceSuggestions");
        migrationBuilder.DropColumn(name: "AppliedAt", table: "SeasonalPriceSuggestions");
    }
}
