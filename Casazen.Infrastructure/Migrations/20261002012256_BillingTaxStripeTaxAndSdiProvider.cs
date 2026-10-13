using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BillingTaxStripeTaxAndSdiProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformBillingMetrics");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "PlatformInvoices",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerCountry",
                table: "PlatformInvoices",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerVatIdVerification",
                table: "PlatformInvoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SdiError",
                table: "PlatformInvoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SdiStatusUpdatedAt",
                table: "PlatformInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeInvoiceNumber",
                table: "PlatformInvoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCountry",
                table: "PlatformInvoices",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxReviewReason",
                table: "PlatformInvoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxabilityReasons",
                table: "PlatformInvoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRatePercent",
                table: "PlatformInvoices",
                type: "numeric(7,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingFiscalCode",
                table: "Orgs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingPecEmail",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingSdiRecipientCode",
                table: "Orgs",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "CustomerCountry",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "CustomerVatIdVerification",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "SdiError",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "SdiStatusUpdatedAt",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "StripeInvoiceNumber",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "TaxCountry",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "TaxReviewReason",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "TaxabilityReasons",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "VatRatePercent",
                table: "PlatformInvoices");

            migrationBuilder.DropColumn(
                name: "BillingFiscalCode",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "BillingPecEmail",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "BillingSdiRecipientCode",
                table: "Orgs");

            migrationBuilder.CreateTable(
                name: "PlatformBillingMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CalendarYear = table.Column<int>(type: "integer", nullable: false),
                    EuB2cCrossBorderRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OssSwitchoverAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OssThresholdReached = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformBillingMetrics", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PlatformBillingMetrics",
                columns: new[] { "Id", "CalendarYear", "EuB2cCrossBorderRevenue", "OssSwitchoverAt", "OssThresholdReached", "UpdatedAt" },
                values: new object[] { 1, 2026, 0m, null, false, new DateTime(2026, 6, 11, 0, 0, 0, 0, DateTimeKind.Utc) });
        }
    }
}
