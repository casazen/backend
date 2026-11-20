using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSwissGuestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "SwissComplianceReceipts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCityName",
                table: "StayGuests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidencePostalCode",
                table: "StayGuests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceStreet",
                table: "StayGuests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                table: "Bookings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResidenceCityName",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidencePostalCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceStreet",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                table: "Bookings");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "SwissComplianceReceipts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);
        }
    }
}
