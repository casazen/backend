using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenapiLeaseRegistrationPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenapiLeaseRegistrationPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeePercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SessionUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenapiLeaseRegistrationPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenapiLeaseRegistrationPayments_LeaseContracts_LeaseContra~",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenapiLeaseRegistrationPayments_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenapiLeaseRegistrationPayments_LeaseContractId",
                table: "OpenapiLeaseRegistrationPayments",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenapiLeaseRegistrationPayments_OrgId_LeaseContractId",
                table: "OpenapiLeaseRegistrationPayments",
                columns: new[] { "OrgId", "LeaseContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenapiLeaseRegistrationPayments_StripeCheckoutSessionId",
                table: "OpenapiLeaseRegistrationPayments",
                column: "StripeCheckoutSessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenapiLeaseRegistrationPayments");
        }
    }
}
