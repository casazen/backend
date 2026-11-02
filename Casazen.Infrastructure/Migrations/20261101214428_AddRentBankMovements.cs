using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRentBankMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RentBankMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentLedgerEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayerIbanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PayerNameHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ValueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentBankMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentBankMovements_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentBankMovements_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentBankMovements_RentLedgerEntries_RentLedgerEntryId",
                        column: x => x.RentLedgerEntryId,
                        principalTable: "RentLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentBankMovements_LeaseContractId",
                table: "RentBankMovements",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_RentBankMovements_OrgId_Fingerprint",
                table: "RentBankMovements",
                columns: new[] { "OrgId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentBankMovements_OrgId_PayerIbanHash",
                table: "RentBankMovements",
                columns: new[] { "OrgId", "PayerIbanHash" });

            migrationBuilder.CreateIndex(
                name: "IX_RentBankMovements_OrgId_PayerNameHash",
                table: "RentBankMovements",
                columns: new[] { "OrgId", "PayerNameHash" });

            migrationBuilder.CreateIndex(
                name: "IX_RentBankMovements_RentLedgerEntryId",
                table: "RentBankMovements",
                column: "RentLedgerEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RentBankMovements");
        }
    }
}
