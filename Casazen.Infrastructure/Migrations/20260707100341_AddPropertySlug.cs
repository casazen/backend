using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertySlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Properties",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_Properties_OrgId_Slug",
                table: "Properties",
                columns: new[] { "OrgId", "Slug" },
                unique: true,
                filter: "\"Slug\" IS NOT NULL");

            migrationBuilder.Sql("""
                UPDATE "Properties"
                SET "Slug" = trim(both '-' from lower(regexp_replace(regexp_replace(trim("Name"), '[^a-zA-Z0-9]+', '-', 'g'), '-{2,}', '-', 'g')))
                    || '-' || substring(replace("Id"::text, '-', ''), 1, 8)
                WHERE "Slug" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_Properties_OrgId_Slug",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Properties");
        }
    }
}
