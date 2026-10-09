using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBusyWindowFeedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc",
                table: "SupplierBusyWindows",
                columns: new[] { "OrgId", "ExternalUid", "StartUtc" },
                unique: true,
                filter: "\"ExternalUid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc",
                table: "SupplierBusyWindows");
        }
    }
}
