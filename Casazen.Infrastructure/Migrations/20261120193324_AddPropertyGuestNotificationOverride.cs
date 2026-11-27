using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyGuestNotificationOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuestNotificationOverride",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_GuestNotificationOverride",
                table: "Properties",
                sql: "\"GuestNotificationOverride\" IS NULL OR \"GuestNotificationOverride\" IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_GuestNotificationOverride",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "GuestNotificationOverride",
                table: "Properties");
        }
    }
}
