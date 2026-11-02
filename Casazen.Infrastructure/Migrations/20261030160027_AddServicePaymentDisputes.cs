using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServicePaymentDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServicePaymentDisputes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    OpenedBy = table.Column<int>(type: "integer", nullable: false),
                    NoShow = table.Column<bool>(type: "boolean", nullable: false),
                    OpenedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CustomerTextEncrypted = table.Column<string>(type: "text", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupplierReplyDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupplierReplyEncrypted = table.Column<string>(type: "text", nullable: true),
                    SupplierRepliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionOverdueAlertedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AdminNoteEncrypted = table.Column<string>(type: "text", nullable: true),
                    DecidedRefundCents = table.Column<int>(type: "integer", nullable: true),
                    RefundTargetCents = table.Column<int>(type: "integer", nullable: true),
                    SettledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundAlertedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePaymentDisputes", x => x.Id);
                    table.CheckConstraint("CK_ServicePaymentDisputes_Refund", "(\"Status\" = 3 AND \"DecidedRefundCents\" IS NOT NULL AND \"DecidedRefundCents\" >= 0 AND \"RefundTargetCents\" IS NOT NULL AND \"RefundTargetCents\" >= \"DecidedRefundCents\") OR (\"DecidedRefundCents\" IS NULL AND \"RefundTargetCents\" IS NULL AND \"SettledAt\" IS NULL)");
                    table.CheckConstraint("CK_ServicePaymentDisputes_Resolved", "(\"Status\" IN (0, 1) AND \"ResolvedAt\" IS NULL) OR (\"Status\" IN (2, 3, 4) AND \"ResolvedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_ServicePaymentDisputes_Status", "\"Status\" BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_ServicePaymentDisputes_Orgs_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServicePaymentDisputes_ServiceRequestPayments_ServiceReques~",
                        column: x => x.ServiceRequestPaymentId,
                        principalTable: "ServiceRequestPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServicePaymentDisputes_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePaymentDisputes_ServiceRequestId",
                table: "ServicePaymentDisputes",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePaymentDisputes_Status_SupplierReplyDueAt",
                table: "ServicePaymentDisputes",
                columns: new[] { "Status", "SupplierReplyDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePaymentDisputes_SupplierOrgId_Status",
                table: "ServicePaymentDisputes",
                columns: new[] { "SupplierOrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UIX_ServicePaymentDisputes_OpenPerPayment",
                table: "ServicePaymentDisputes",
                column: "ServiceRequestPaymentId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServicePaymentDisputes");
        }
    }
}
