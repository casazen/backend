using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyTaxpayerFiscalCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyFiscalYears_OrgId_TaxYear",
                table: "PropertyFiscalYears");

            migrationBuilder.AddColumn<string>(
                name: "TaxpayerFiscalCode",
                table: "Properties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiscalYears_OrgId_TaxYear",
                table: "PropertyFiscalYears",
                columns: new[] { "OrgId", "TaxYear" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyFiscalYears_OrgId_TaxYear",
                table: "PropertyFiscalYears");

            migrationBuilder.DropColumn(
                name: "TaxpayerFiscalCode",
                table: "Properties");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiscalYears_OrgId_TaxYear",
                table: "PropertyFiscalYears",
                columns: new[] { "OrgId", "TaxYear" },
                unique: true,
                filter: "\"IsPrimaryForCedolare\" = TRUE");
        }
    }
}
