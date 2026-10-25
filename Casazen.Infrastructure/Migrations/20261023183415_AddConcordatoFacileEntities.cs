using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConcordatoFacileEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConcordatoFacileGifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedBySubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedForLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcordatoFacileGifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConcordatoFacileGifts_LeaseContracts_ConsumedForLeaseId",
                        column: x => x.ConsumedForLeaseId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcordatoFacileGifts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConcordatoFacilePurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcordatoFacilePurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConcordatoFacilePurchases_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcordatoFacilePurchases_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoFacileGifts_ConsumedForLeaseId",
                table: "ConcordatoFacileGifts",
                column: "ConsumedForLeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoFacileGifts_OrgId",
                table: "ConcordatoFacileGifts",
                column: "OrgId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoFacilePurchases_LeaseContractId",
                table: "ConcordatoFacilePurchases",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoFacilePurchases_OrgId_LeaseContractId",
                table: "ConcordatoFacilePurchases",
                columns: new[] { "OrgId", "LeaseContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoFacilePurchases_StripePaymentIntentId",
                table: "ConcordatoFacilePurchases",
                column: "StripePaymentIntentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConcordatoFacileGifts");

            migrationBuilder.DropTable(
                name: "ConcordatoFacilePurchases");
        }
    }
}
