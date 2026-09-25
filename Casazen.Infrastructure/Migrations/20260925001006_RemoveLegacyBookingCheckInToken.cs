using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyBookingCheckInToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CheckInTokenExpiresAt",
                table: "Bookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckInToken",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckInTokenExpiresAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings",
                column: "CheckInToken",
                unique: true,
                filter: "\"CheckInToken\" IS NOT NULL");
        }
    }
}
