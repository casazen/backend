using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerMandate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerMandates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommissionPercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    CleaningFlatRate = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    LinenFlatRate = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    PassthroughPaymentFees = table.Column<bool>(type: "boolean", nullable: false),
                    WithholdingTreatment = table.Column<int>(type: "integer", nullable: false),
                    MonthlyFrequency = table.Column<int>(type: "integer", nullable: false),
                    MandatePdfUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerMandates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerMandates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerMandates_PropertyOwnerships_PropertyOwnershipId",
                        column: x => x.PropertyOwnershipId,
                        principalTable: "PropertyOwnerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerMandates_OrgId",
                table: "OwnerMandates",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerMandates_PropertyOwnershipId",
                table: "OwnerMandates",
                column: "PropertyOwnershipId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerMandates");
        }
    }
}
