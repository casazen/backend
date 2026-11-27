using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRentLedgerComponentsAndLeaseCondominiumTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeaseCondominiumTerms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdvanceMode = table.Column<int>(type: "integer", nullable: false),
                    AdvanceAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Cadence = table.Column<int>(type: "integer", nullable: true),
                    SettlementRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ProrationRule = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExtractionConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseCondominiumTerms", x => x.Id);
                    table.CheckConstraint("CK_LeaseCondominiumTerms_Confidence", "\"ExtractionConfidence\" IS NULL OR (\"ExtractionConfidence\" >= 0 AND \"ExtractionConfidence\" <= 1)");
                    table.ForeignKey(
                        name: "FK_LeaseCondominiumTerms_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseCondominiumTerms_LeaseDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "LeaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LeaseCondominiumTerms_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RentLedgerComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentLedgerEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CompetenceStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CompetenceEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    TenantSettlementId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentLedgerComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentLedgerComponents_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentLedgerComponents_RentLedgerEntries_RentLedgerEntryId",
                        column: x => x.RentLedgerEntryId,
                        principalTable: "RentLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseCondominiumTerms_LeaseContractId",
                table: "LeaseCondominiumTerms",
                column: "LeaseContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseCondominiumTerms_OrgId",
                table: "LeaseCondominiumTerms",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseCondominiumTerms_SourceDocumentId",
                table: "LeaseCondominiumTerms",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerComponents_OrgId",
                table: "RentLedgerComponents",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerComponents_RentLedgerEntryId",
                table: "RentLedgerComponents",
                column: "RentLedgerEntryId");

            // Existing installments had no breakdown: the whole amount due is rent, so AmountDue stays the sum.
            migrationBuilder.Sql(
                """
                INSERT INTO "RentLedgerComponents" (
                    "Id", "OrgId", "RentLedgerEntryId", "Kind", "Amount", "CompetenceStart", "CompetenceEnd")
                SELECT gen_random_uuid(), "OrgId", "Id", 0, "AmountDue", "PeriodStart", "PeriodEnd"
                FROM "RentLedgerEntries";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeaseCondominiumTerms");

            migrationBuilder.DropTable(
                name: "RentLedgerComponents");
        }
    }
}
