using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateBookingImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BookingsImportedThrough",
                table: "OctorateListingMappings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OctorateReservationId",
                table: "Bookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OctorateUpdatedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtaBalanceAmount",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtaCommission",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtaNetAmount",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtaPaidAmount",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_Bookings_OrgId_OctorateReservationId",
                table: "Bookings",
                columns: new[] { "OrgId", "OctorateReservationId" },
                unique: true,
                filter: "\"OctorateReservationId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_Bookings_OrgId_OctorateReservationId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BookingsImportedThrough",
                table: "OctorateListingMappings");

            migrationBuilder.DropColumn(
                name: "OctorateReservationId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OctorateUpdatedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaBalanceAmount",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaCommission",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaNetAmount",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaPaidAmount",
                table: "Bookings");
        }
    }
}
