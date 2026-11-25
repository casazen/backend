using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceActivationVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Properties",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AddColumn<DateTime>(
                name: "CalendarImportRequestedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MappingCommittedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarkConnectedRequestedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservationsImportAcceptedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservationsImportRequestedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RoomsImportRequestedAt",
                table: "MarketplaceActivations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarImportRequestedAt",
                table: "MarketplaceActivations");

            migrationBuilder.DropColumn(
                name: "MappingCommittedAt",
                table: "MarketplaceActivations");

            migrationBuilder.DropColumn(
                name: "MarkConnectedRequestedAt",
                table: "MarketplaceActivations");

            migrationBuilder.DropColumn(
                name: "ReservationsImportAcceptedAt",
                table: "MarketplaceActivations");

            migrationBuilder.DropColumn(
                name: "ReservationsImportRequestedAt",
                table: "MarketplaceActivations");

            migrationBuilder.DropColumn(
                name: "RoomsImportRequestedAt",
                table: "MarketplaceActivations");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Properties",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000);
        }
    }
}
