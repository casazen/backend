using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierPaymentRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CommissionOverrideUntil",
                table: "SupplierProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceRequestPaymentRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    StripeRefundId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApplicationFeeRefundedCents = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequestPaymentRefunds", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequestPaymentRefunds_Amounts", "\"AmountCents\" BETWEEN 1 AND 10000000 AND \"Sequence\" >= 1 AND (\"ApplicationFeeRefundedCents\" IS NULL OR \"ApplicationFeeRefundedCents\" >= 0)");
                    table.CheckConstraint("CK_ServiceRequestPaymentRefunds_Succeeded", "\"Status\" <> 1 OR \"CompletedAt\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ServiceRequestPaymentRefunds_ServiceRequestPayments_Service~",
                        column: x => x.ServiceRequestPaymentId,
                        principalTable: "ServiceRequestPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierProfiles_CommissionOverrideUntil",
                table: "SupplierProfiles",
                sql: "\"CommissionOverrideUntil\" IS NULL OR \"CommissionPercentOverride\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestPaymentRefunds_Status",
                table: "ServiceRequestPaymentRefunds",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPaymentRefunds_IdempotencyKey",
                table: "ServiceRequestPaymentRefunds",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPaymentRefunds_Payment_Sequence",
                table: "ServiceRequestPaymentRefunds",
                columns: new[] { "ServiceRequestPaymentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPaymentRefunds_StripeRefundId",
                table: "ServiceRequestPaymentRefunds",
                column: "StripeRefundId",
                unique: true,
                filter: "\"StripeRefundId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceRequestPaymentRefunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierProfiles_CommissionOverrideUntil",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "CommissionOverrideUntil",
                table: "SupplierProfiles");
        }
    }
}
