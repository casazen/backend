using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyDoorToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DoorToken",
                table: "Properties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BiometricConsentTextHash",
                table: "GuestCheckInSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_Properties_DoorToken",
                table: "Properties",
                column: "DoorToken",
                unique: true,
                filter: "\"DoorToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_Properties_DoorToken",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "DoorToken",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "BiometricConsentTextHash",
                table: "GuestCheckInSessions");
        }
    }
}
