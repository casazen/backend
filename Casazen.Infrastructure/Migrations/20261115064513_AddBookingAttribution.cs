using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributionCampaign",
                table: "Bookings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionMedium",
                table: "Bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionReferrerHost",
                table: "Bookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionSource",
                table: "Bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributionCampaign",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionMedium",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionReferrerHost",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionSource",
                table: "Bookings");
        }
    }
}
