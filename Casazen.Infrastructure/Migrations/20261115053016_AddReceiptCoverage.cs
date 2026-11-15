using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualComplianceProofProperties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProofId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualComplianceProofProperties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualComplianceProofProperties_ManualComplianceProofs_Proo~",
                        column: x => x.ProofId,
                        principalTable: "ManualComplianceProofs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualComplianceProofProperties_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualComplianceProofProperties_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualComplianceProofProperties_OrgId_PropertyId",
                table: "ManualComplianceProofProperties",
                columns: new[] { "OrgId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualComplianceProofProperties_ProofId_PropertyId",
                table: "ManualComplianceProofProperties",
                columns: new[] { "ProofId", "PropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualComplianceProofProperties_PropertyId",
                table: "ManualComplianceProofProperties",
                column: "PropertyId");

            // E20/F02/US01/T01 (decision O3): the proofs that exist keep their single flat. One line for each of them, for the flat it was filed under, so the table
            // says for every proof which flats it covers from the first day. The unique pair (ProofId, PropertyId) makes a repeat of the statement harmless.
            migrationBuilder.Sql(
                """
                INSERT INTO "ManualComplianceProofProperties" ("Id", "OrgId", "ProofId", "PropertyId")
                SELECT gen_random_uuid(), proof."OrgId", proof."Id", proof."PropertyId"
                FROM "ManualComplianceProofs" AS proof
                ON CONFLICT ("ProofId", "PropertyId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualComplianceProofProperties");
        }
    }
}
