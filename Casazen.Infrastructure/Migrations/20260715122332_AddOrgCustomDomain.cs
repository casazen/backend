using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgCustomDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomDomain",
                table: "Orgs",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DomainVerificationStatus",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DomainVerificationToken",
                table: "Orgs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicHostMode",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Subdomain",
                table: "Orgs",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_Subdomain",
                table: "Orgs",
                column: "Subdomain",
                unique: true,
                filter: "\"Subdomain\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs");

            migrationBuilder.DropIndex(
                name: "IX_Orgs_Subdomain",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "CustomDomain",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVerificationStatus",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVerificationToken",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "PublicHostMode",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "Subdomain",
                table: "Orgs");
        }
    }
}
