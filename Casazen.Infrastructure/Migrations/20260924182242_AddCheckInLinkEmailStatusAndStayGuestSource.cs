using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckInLinkEmailStatusAndStayGuestSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataSource",
                table: "StayGuests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EnteredByUserId",
                table: "StayGuests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkEmailError",
                table: "GuestCheckInSessions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkEmailStatus",
                table: "GuestCheckInSessions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataSource",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "EnteredByUserId",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "LinkEmailError",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "LinkEmailStatus",
                table: "GuestCheckInSessions");
        }
    }
}
