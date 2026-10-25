using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseTransitoryNeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransitoryDocumentation",
                table: "LeaseContracts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransitoryNeed",
                table: "LeaseContracts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransitoryParty",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransitoryDocumentation",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "TransitoryNeed",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "TransitoryParty",
                table: "LeaseContracts");
        }
    }
}
