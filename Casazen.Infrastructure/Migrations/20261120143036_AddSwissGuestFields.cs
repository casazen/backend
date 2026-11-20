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
        }
    }
}
