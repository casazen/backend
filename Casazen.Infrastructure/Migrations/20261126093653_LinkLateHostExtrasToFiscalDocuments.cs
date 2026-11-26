using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkLateHostExtrasToFiscalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupplementToDocumentId",
                table: "GuestHostExtraOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplementForExtraOrderId",
                table: "FiscalDocuments",
                type: "uuid",
                nullable: true);

            // Only reconcile historical orders created after the latest eligible invoice was issued.
            migrationBuilder.Sql("""
                UPDATE "GuestHostExtraOrders" AS o
                SET "SupplementToDocumentId" = (
                    SELECT d."Id" FROM "FiscalDocuments" AS d
                    JOIN "OrgFiscalIssuerProfiles" AS p ON p."Id" = d."IssuerProfileId" AND p."OrgId" = o."OrgId"
                    WHERE d."OrgId" = o."OrgId" AND d."BookingId" = o."BookingId" AND d."Purpose" = 1
                      AND d."Source" = 1 AND d."Status" = 2 AND d."Kind" IN (1, 2) AND NOT d."CreditedInFull"
                      AND p."PropertyOwnerId" IS NULL AND d."Currency" = o."Currency"
                      AND d."UpdatedAt" < o."CreatedAt"
                      AND NOT EXISTS (
                          SELECT 1 FROM "FiscalDocuments" AS later
                          WHERE later."BookingId" = o."BookingId" AND later."OrgId" = o."OrgId"
                            AND later."Purpose" = 1 AND later."Status" = 2 AND later."Kind" IN (1, 2)
                            AND later."UpdatedAt" >= o."CreatedAt")
                    ORDER BY d."ForAgencyServices" DESC, d."UpdatedAt" DESC LIMIT 1)
                WHERE o."Status" IN (2, 4);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_GuestHostExtraOrders_SupplementToDocumentId",
                table: "GuestHostExtraOrders",
                column: "SupplementToDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_SupplementForExtraOrderId",
                table: "FiscalDocuments",
                column: "SupplementForExtraOrderId",
                unique: true,
                filter: "\"SupplementForExtraOrderId\" IS NOT NULL AND \"Status\" <> 3");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalDocuments_GuestHostExtraOrders_SupplementForExtraOrde~",
                table: "FiscalDocuments",
                column: "SupplementForExtraOrderId",
                principalTable: "GuestHostExtraOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GuestHostExtraOrders_FiscalDocuments_SupplementToDocumentId",
                table: "GuestHostExtraOrders",
                column: "SupplementToDocumentId",
                principalTable: "FiscalDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiscalDocuments_GuestHostExtraOrders_SupplementForExtraOrde~",
                table: "FiscalDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_GuestHostExtraOrders_FiscalDocuments_SupplementToDocumentId",
                table: "GuestHostExtraOrders");

            migrationBuilder.DropIndex(
                name: "IX_GuestHostExtraOrders_SupplementToDocumentId",
                table: "GuestHostExtraOrders");

            migrationBuilder.DropIndex(
                name: "IX_FiscalDocuments_SupplementForExtraOrderId",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "SupplementToDocumentId",
                table: "GuestHostExtraOrders");

            migrationBuilder.DropColumn(
                name: "SupplementForExtraOrderId",
                table: "FiscalDocuments");
        }
    }
}
