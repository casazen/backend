using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierCalendarSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CalendarLastSyncAt",
                table: "SupplierProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarSyncError",
                table: "SupplierProfiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CalendarSyncType",
                table: "SupplierProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GoogleCalendarRefreshToken",
                table: "SupplierProfiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IcalFeedUrl",
                table: "SupplierProfiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarLastSyncAt",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "CalendarSyncError",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "CalendarSyncType",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarRefreshToken",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "IcalFeedUrl",
                table: "SupplierProfiles");
        }
    }
}
