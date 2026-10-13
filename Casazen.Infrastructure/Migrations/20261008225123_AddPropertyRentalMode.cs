using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PM-01 (decision D19): <c>Properties.RentalMode</c> (0 = short stays, 1 = long-term leases; append only) with the
    /// index <c>(OrgId, RentalMode)</c> of the lists. Every existing row gets 0, then the backfill of
    /// <c>AddPropertyRentalMode.Data.cs</c> sets 1 on the properties with lease contracts, no booking and the A7-06 marker
    /// (no guests, no nightly rate); the ambiguous ones stay short. Runbook, dry run and rollback:
    /// <c>docs/runbooks/property-rental-mode.md</c>.
    /// </summary>
    public partial class AddPropertyRentalMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RentalMode",
                table: "Properties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OrgId_RentalMode",
                table: "Properties",
                columns: new[] { "OrgId", "RentalMode" });

            ApplyData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Properties_OrgId_RentalMode",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "RentalMode",
                table: "Properties");
        }
    }
}
