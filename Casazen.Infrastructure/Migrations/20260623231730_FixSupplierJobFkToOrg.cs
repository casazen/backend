using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixSupplierJobFkToOrg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierJobs_SupplierProfiles_SupplierOrgId",
                table: "SupplierJobs");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierJobs_Orgs_SupplierOrgId",
                table: "SupplierJobs",
                column: "SupplierOrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierJobs_Orgs_SupplierOrgId",
                table: "SupplierJobs");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierJobs_SupplierProfiles_SupplierOrgId",
                table: "SupplierJobs",
                column: "SupplierOrgId",
                principalTable: "SupplierProfiles",
                principalColumn: "OrgId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
