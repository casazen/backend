using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenapiRegistrationPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderCode",
                table: "LeaseRegistrations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenapiRegistrationData",
                table: "LeaseContracts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenapiRegistrationVersion",
                table: "LeaseContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderCode",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "OpenapiRegistrationData",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "OpenapiRegistrationVersion",
                table: "LeaseContracts");
        }
    }
}
