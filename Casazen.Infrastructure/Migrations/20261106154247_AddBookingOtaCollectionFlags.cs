using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingOtaCollectionFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OtaCommissionIncludedInPayments",
                table: "Bookings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OtaCompanyCollect",
                table: "Bookings",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OtaCommissionIncludedInPayments",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaCompanyCollect",
                table: "Bookings");
        }
    }
}
