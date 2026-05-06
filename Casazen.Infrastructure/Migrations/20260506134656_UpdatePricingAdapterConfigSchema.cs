using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePricingAdapterConfigSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Frequency",
                table: "PricingAdapterConfigs",
                newName: "AdaptationFrequency");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PricingHistories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PricingAdapterConfigs",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IncludePublicHolidays",
                table: "PricingAdapterConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeSeasonality",
                table: "PricingAdapterConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "PricingAdapterConfigs",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_PricingAdapterConfigs_PropertyId",
                table: "PricingAdapterConfigs",
                column: "PropertyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PricingAdapterConfigs_PropertyId",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PricingHistories");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "IncludePublicHolidays",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "IncludeSeasonality",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "PricingAdapterConfigs");

            migrationBuilder.RenameColumn(
                name: "AdaptationFrequency",
                table: "PricingAdapterConfigs",
                newName: "Frequency");
        }
    }
}
