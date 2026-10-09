using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CommissionPercentOverride",
                table: "SupplierProfiles",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinalAmountConfirmedAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidBy",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentMode",
                table: "ServiceRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ServiceRequestPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayerKind = table.Column<int>(type: "integer", nullable: false),
                    PayerOrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CommissionPercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    ApplicationFeeCents = table.Column<int>(type: "integer", nullable: false),
                    NetCents = table.Column<int>(type: "integer", nullable: false),
                    FeeVatMode = table.Column<int>(type: "integer", nullable: true),
                    FeeVatCents = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConnectedAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PaymentIntentCount = table.Column<int>(type: "integer", nullable: false),
                    PaymentTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentCount = table.Column<int>(type: "integer", nullable: false),
                    LateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidVia = table.Column<int>(type: "integer", nullable: true),
                    RefundedCents = table.Column<int>(type: "integer", nullable: false),
                    LineItemsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    OfflineNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MarkedPaidByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CanceledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequestPayments", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequestPayments_Amounts", "\"AmountCents\" BETWEEN 1 AND 10000000 AND \"ApplicationFeeCents\" >= 0 AND \"ApplicationFeeCents\" < \"AmountCents\" AND \"NetCents\" = \"AmountCents\" - \"ApplicationFeeCents\" AND \"RefundedCents\" BETWEEN 0 AND \"AmountCents\" AND \"PaymentIntentCount\" >= 0 AND \"SentCount\" >= 0");
                    table.CheckConstraint("CK_ServiceRequestPayments_CommissionPercent", "\"CommissionPercent\" BETWEEN 0 AND 50");
                    table.CheckConstraint("CK_ServiceRequestPayments_FeeVat", "(\"FeeVatMode\" IS NULL AND \"FeeVatCents\" IS NULL) OR (\"FeeVatMode\" IS NOT NULL AND \"FeeVatCents\" IS NOT NULL AND \"FeeVatCents\" >= 0)");
                    table.CheckConstraint("CK_ServiceRequestPayments_Paid", "\"Status\" NOT IN (2, 5, 6) OR (\"PaidAt\" IS NOT NULL AND \"PaidVia\" IS NOT NULL)");
                    table.CheckConstraint("CK_ServiceRequestPayments_Payer", "(\"PayerKind\" = 0 AND \"PayerOrgId\" IS NOT NULL) OR \"PayerKind\" = 1");
                    table.ForeignKey(
                        name: "FK_ServiceRequestPayments_Orgs_PayerOrgId",
                        column: x => x.PayerOrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequestPayments_Orgs_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequestPayments_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierProfiles_CommissionPercentOverride",
                table: "SupplierProfiles",
                sql: "\"CommissionPercentOverride\" IS NULL OR \"CommissionPercentOverride\" BETWEEN 0 AND 50");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestPayments_PayerOrgId",
                table: "ServiceRequestPayments",
                column: "PayerOrgId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestPayments_SupplierOrgId_Status",
                table: "ServiceRequestPayments",
                columns: new[] { "SupplierOrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPayments_ServiceRequestId_Live",
                table: "ServiceRequestPayments",
                column: "ServiceRequestId",
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPayments_StripePaymentIntentId",
                table: "ServiceRequestPayments",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceRequestPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierProfiles_CommissionPercentOverride",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "CommissionPercentOverride",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "FinalAmountConfirmedAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "PaidBy",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "PaymentMode",
                table: "ServiceRequests");
        }
    }
}
