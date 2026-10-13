using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupplierShowcaseSlugUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UIX_SupplierProfiles_ShowcaseSlug",
                table: "SupplierProfiles",
                column: "ShowcaseSlug",
                unique: true,
                filter: "\"ShowcaseSlug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_SupplierProfiles_ShowcaseSlug",
                table: "SupplierProfiles");
        }
    }
}
