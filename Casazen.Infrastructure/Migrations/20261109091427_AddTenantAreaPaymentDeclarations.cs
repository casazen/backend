using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAreaPaymentDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantPaymentDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentLedgerEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TransferDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BankReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReceiptStorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPaymentDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPaymentDeclarations_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantPaymentDeclarations_RentLedgerEntries_RentLedgerEntry~",
                        column: x => x.RentLedgerEntryId,
                        principalTable: "RentLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentDeclarations_LeaseContractId",
                table: "TenantPaymentDeclarations",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentDeclarations_OrgId",
                table: "TenantPaymentDeclarations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentDeclarations_RentLedgerEntryId_SubmittedAt",
                table: "TenantPaymentDeclarations",
                columns: new[] { "RentLedgerEntryId", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPaymentDeclarations");
        }
    }
}
