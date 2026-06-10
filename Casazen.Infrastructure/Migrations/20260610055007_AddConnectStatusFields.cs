using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConnectChargesEnabled",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ConnectDetailsSubmitted",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ConnectPayoutsEnabled",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ConnectRequirementsDueJson",
                table: "Orgs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectChargesEnabled",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "ConnectDetailsSubmitted",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "ConnectPayoutsEnabled",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "ConnectRequirementsDueJson",
                table: "Orgs");
        }
    }
}
