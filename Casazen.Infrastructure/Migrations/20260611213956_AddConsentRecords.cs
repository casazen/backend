using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentRecords : Migration
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

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentPeriodEnd",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PastDueSince",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
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
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VatIdValidatedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConsentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformBillingMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CalendarYear = table.Column<int>(type: "integer", nullable: false),
                    EuB2cCrossBorderRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OssThresholdReached = table.Column<bool>(type: "boolean", nullable: false),
                    OssSwitchoverAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformBillingMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeInvoiceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AmountExVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatTreatment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OssApplied = table.Column<bool>(type: "boolean", nullable: false),
                    SdiStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SdiTransmissionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FatturaPaXmlUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformInvoices_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedStripeEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedStripeEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "RentSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cadence = table.Column<int>(type: "integer", nullable: false),
                    BillingDayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NextRunDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LandlordStripeAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MandateReference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentSchedules_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentSchedules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RentLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountDue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConnectedAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IsVatExempt = table.Column<bool>(type: "boolean", nullable: false),
                    StampDutyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceiptStoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChargedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentLedgerEntries_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentLedgerEntries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentLedgerEntries_RentSchedules_RentScheduleId",
                        column: x => x.RentScheduleId,
                        principalTable: "RentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "PlatformBillingMetrics",
                columns: new[] { "Id", "CalendarYear", "EuB2cCrossBorderRevenue", "OssSwitchoverAt", "OssThresholdReached", "UpdatedAt" },
                values: new object[] { 1, 2026, 0m, null, false, new DateTime(2026, 6, 11, 21, 39, 55, 821, DateTimeKind.Utc).AddTicks(3582) });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "rent.manage", 2 },
                    { "rent.read", 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_StripeCustomerId",
                table: "Orgs",
                column: "StripeCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_UserId_OrgId_Type",
                table: "ConsentRecords",
                columns: new[] { "UserId", "OrgId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformInvoices_OrgId",
                table: "PlatformInvoices",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformInvoices_SdiStatus",
                table: "PlatformInvoices",
                column: "SdiStatus");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformInvoices_StripeInvoiceId",
                table: "PlatformInvoices",
                column: "StripeInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerEntries_LeaseContractId_PeriodStart",
                table: "RentLedgerEntries",
                columns: new[] { "LeaseContractId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerEntries_OrgId",
                table: "RentLedgerEntries",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerEntries_RentScheduleId",
                table: "RentLedgerEntries",
                column: "RentScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_RentSchedules_LeaseContractId",
                table: "RentSchedules",
                column: "LeaseContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentSchedules_OrgId",
                table: "RentSchedules",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentRecords");

            migrationBuilder.DropTable(
                name: "PlatformBillingMetrics");

            migrationBuilder.DropTable(
                name: "PlatformInvoices");

            migrationBuilder.DropTable(
                name: "ProcessedStripeEvents");

            migrationBuilder.DropTable(
                name: "RentLedgerEntries");

            migrationBuilder.DropTable(
                name: "RentSchedules");

            migrationBuilder.DropIndex(
                name: "IX_Orgs_StripeCustomerId",
                table: "Orgs");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "rent.manage", 2 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "rent.read", 2 });

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "CurrentPeriodEnd",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "PastDueSince",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "VatId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "VatIdValidatedAt",
                table: "Orgs");
        }
    }
}
