using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ComplianceCriticalFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #36 Tourist Tax: split TotalPrice into BasePrice + TouristTax
            migrationBuilder.AddColumn<decimal>(
                name: "BasePrice",
                table: "Bookings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TouristTax",
                table: "Bookings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Migrate: existing TotalPrice becomes BasePrice (TouristTax defaults to 0)
            migrationBuilder.Sql("UPDATE Bookings SET BasePrice = TotalPrice");

            // #36 Tourist Tax rates table
            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Region = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RatePerNight = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxNights = table.Column<int>(type: "int", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_City_EffectiveFrom",
                table: "TaxRates",
                columns: new[] { "City", "EffectiveFrom" });

            // #38 Alloggiati Web - Guest required fields (D.L. 286/1998, Art. 7)
            migrationBuilder.AddColumn<DateTime>(
                name: "DateOfBirth",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirth",
                table: "Guests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Nationality",
                table: "Guests",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DocumentType",
                table: "Guests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "Guests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentIssuingCountry",
                table: "Guests",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentIssueDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentExpiryDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Gender",
                table: "Guests",
                type: "int",
                nullable: true);

            // #37 GDPR Compliance Fields (Articles 6, 7, 13-17)
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentVersion",
                table: "Guests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "MarketingConsent",
                table: "Guests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketingConsentDate",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataRetentionUntil",
                table: "Guests",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "DATEADD(YEAR, 7, GETUTCDATE())");

            migrationBuilder.AddColumn<string>(
                name: "DataProcessingPurpose",
                table: "Guests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Booking Management");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Guests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Guests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Guests",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            // #38 Alloggiati Web reports table
            migrationBuilder.CreateTable(
                name: "AlloggiatiWebReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ConfirmationNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlloggiatiWebReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlloggiatiWebReports_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlloggiatiWebReports_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_BookingId",
                table: "AlloggiatiWebReports",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_Status",
                table: "AlloggiatiWebReports",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AlloggiatiWebReports");
            migrationBuilder.DropTable(name: "TaxRates");

            migrationBuilder.DropColumn(name: "BasePrice", table: "Bookings");
            migrationBuilder.DropColumn(name: "TouristTax", table: "Bookings");

            migrationBuilder.DropColumn(name: "DateOfBirth", table: "Guests");
            migrationBuilder.DropColumn(name: "PlaceOfBirth", table: "Guests");
            migrationBuilder.DropColumn(name: "Nationality", table: "Guests");
            migrationBuilder.DropColumn(name: "DocumentType", table: "Guests");
            migrationBuilder.DropColumn(name: "DocumentNumber", table: "Guests");
            migrationBuilder.DropColumn(name: "DocumentIssuingCountry", table: "Guests");
            migrationBuilder.DropColumn(name: "DocumentIssueDate", table: "Guests");
            migrationBuilder.DropColumn(name: "DocumentExpiryDate", table: "Guests");
            migrationBuilder.DropColumn(name: "Gender", table: "Guests");
            migrationBuilder.DropColumn(name: "ConsentDate", table: "Guests");
            migrationBuilder.DropColumn(name: "ConsentVersion", table: "Guests");
            migrationBuilder.DropColumn(name: "MarketingConsent", table: "Guests");
            migrationBuilder.DropColumn(name: "MarketingConsentDate", table: "Guests");
            migrationBuilder.DropColumn(name: "DataRetentionUntil", table: "Guests");
            migrationBuilder.DropColumn(name: "DataProcessingPurpose", table: "Guests");
            migrationBuilder.DropColumn(name: "IsDeleted", table: "Guests");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "Guests");
            migrationBuilder.DropColumn(name: "DeletionReason", table: "Guests");
        }
    }
}
