using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStrFiscalRegime2026 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NetAmountAfterWithholding",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OtaWithholdingTax",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "WithholdingSource",
                table: "Payments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WithholdingTaxApplied",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FiscalCode",
                table: "Orgs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalDataRetentionUntil",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasPartitaIva",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PartitaIvaNumber",
                table: "Orgs",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PropertyFiscalYears",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxYear = table.Column<int>(type: "integer", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    IsPrimaryForCedolare = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyFiscalYears", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyFiscalYears_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyFiscalYears_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiscalYears_OrgId",
                table: "PropertyFiscalYears",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiscalYears_OrgId_TaxYear",
                table: "PropertyFiscalYears",
                columns: new[] { "OrgId", "TaxYear" },
                unique: true,
                filter: "\"IsPrimaryForCedolare\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiscalYears_PropertyId_TaxYear",
                table: "PropertyFiscalYears",
                columns: new[] { "PropertyId", "TaxYear" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyFiscalYears");

            migrationBuilder.DropColumn(
                name: "NetAmountAfterWithholding",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OtaWithholdingTax",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "WithholdingSource",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "WithholdingTaxApplied",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "FiscalCode",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "FiscalDataRetentionUntil",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "HasPartitaIva",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "PartitaIvaNumber",
                table: "Orgs");
        }
    }
}
