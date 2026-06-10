using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAlloggiatiCheckInMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingCountry",
                table: "Orgs",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillingSeats",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionCurrentPeriodEnd",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionPastDueAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionStatus",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VatId",
                table: "Orgs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentScanUrl",
                table: "Guests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckInToken",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManuallyCompleted",
                table: "AlloggiatiWebReports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BillingOssCounters",
                columns: table => new
                {
                    CalendarYear = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EuB2cRevenueEur = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    OssActive = table.Column<bool>(type: "boolean", nullable: false),
                    OssActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingOssCounters", x => x.CalendarYear);
                });

            migrationBuilder.CreateTable(
                name: "PlatformInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeInvoiceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AmountExclVat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CustomerCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CustomerVatId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReverseCharge = table.Column<bool>(type: "boolean", nullable: false),
                    OssApplied = table.Column<bool>(type: "boolean", nullable: false),
                    SdiStatus = table.Column<int>(type: "integer", nullable: false),
                    SdiExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformInvoices_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyQuesturaCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WsKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyQuesturaCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyQuesturaCredentials_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StripeWebhookEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeWebhookEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_StripeCustomerId",
                table: "Orgs",
                column: "StripeCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_SubscriptionId",
                table: "Orgs",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings",
                column: "CheckInToken",
                unique: true,
                filter: "\"CheckInToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformInvoices_OrgId",
                table: "PlatformInvoices",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformInvoices_StripeInvoiceId",
                table: "PlatformInvoices",
                column: "StripeInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyQuesturaCredentials_PropertyId",
                table: "PropertyQuesturaCredentials",
                column: "PropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookEvents_ProcessedAt",
                table: "StripeWebhookEvents",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingOssCounters");

            migrationBuilder.DropTable(
                name: "PlatformInvoices");

            migrationBuilder.DropTable(
                name: "PropertyQuesturaCredentials");

            migrationBuilder.DropTable(
                name: "StripeWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Orgs_StripeCustomerId",
                table: "Orgs");

            migrationBuilder.DropIndex(
                name: "IX_Orgs_SubscriptionId",
                table: "Orgs");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "BillingSeats",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionCurrentPeriodEnd",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionPastDueAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "VatId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DocumentScanUrl",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ManuallyCompleted",
                table: "AlloggiatiWebReports");
        }
    }
}
