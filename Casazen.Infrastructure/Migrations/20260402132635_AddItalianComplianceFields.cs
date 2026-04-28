using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddItalianComplianceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsentIpAddress",
                table: "Guests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DataAnonymizedDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataProcessingConsentDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataRetentionExpiryDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateOfBirth",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentExpiryDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentIssueDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentIssuingCountry",
                table: "Guests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "Guests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                table: "Guests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ErasureRequested",
                table: "Guests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ErasureRequestedDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketingConsentDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nationality",
                table: "Guests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirth",
                table: "Guests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NumberOfAdults",
                table: "Bookings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NumberOfChildren",
                table: "Bookings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TouristTaxAmount",
                table: "Bookings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TouristTaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RegionCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RatePerPersonPerNight = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxNights = table.Column<int>(type: "int", nullable: true),
                    MinimumAge = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TouristTaxRates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Guests_Email",
                table: "Guests",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_TouristTaxRates_City",
                table: "TouristTaxRates",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_TouristTaxRates_City_IsActive_EffectiveFrom",
                table: "TouristTaxRates",
                columns: new[] { "City", "IsActive", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TouristTaxRates");

            migrationBuilder.DropIndex(
                name: "IX_Guests_Email",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "ConsentIpAddress",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DataAnonymizedDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DataProcessingConsentDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DataRetentionExpiryDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DocumentExpiryDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DocumentIssueDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DocumentIssuingCountry",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "ErasureRequested",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "ErasureRequestedDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "MarketingConsentDate",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "Nationality",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "PlaceOfBirth",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "NumberOfAdults",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "NumberOfChildren",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "TouristTaxAmount",
                table: "Bookings");
        }
    }
}
