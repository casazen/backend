using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHostSurfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GtmContainerId",
                table: "Orgs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GuestAreaOnOwnDomain",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OwnerLoginHostState",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "OwnerPortalOnOwnDomain",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseOwnDomainForLinks",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GtmContainerId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "GuestAreaOnOwnDomain",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "OwnerLoginHostState",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "OwnerPortalOnOwnDomain",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "UseOwnDomainForLinks",
                table: "Orgs");
        }
    }
}
