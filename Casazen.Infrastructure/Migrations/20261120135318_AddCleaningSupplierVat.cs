using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCleaningSupplierVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierVat",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupplierVatAnsweredAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierVatAnsweredByUserId",
                table: "ServiceRequests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupplierVatAskedAgainAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SupplierVatRatePercent",
                table: "ServiceRequests",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupplierVatDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliesVat = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierVatDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierVatDefaults_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierVatDefaults_Orgs_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_SupplierVat",
                table: "ServiceRequests",
                sql: "(\"SupplierVat\" = 1 AND \"SupplierVatRatePercent\" IS NOT NULL) OR (\"SupplierVat\" IS DISTINCT FROM 1 AND \"SupplierVatRatePercent\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierVatDefaults_SupplierOrgId",
                table: "SupplierVatDefaults",
                column: "SupplierOrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierVatDefaults_OrgId_SupplierOrgId",
                table: "SupplierVatDefaults",
                columns: new[] { "OrgId", "SupplierOrgId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierVatDefaults");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_SupplierVat",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierVat",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierVatAnsweredAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierVatAnsweredByUserId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierVatAskedAgainAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierVatRatePercent",
                table: "ServiceRequests");
        }
    }
}
