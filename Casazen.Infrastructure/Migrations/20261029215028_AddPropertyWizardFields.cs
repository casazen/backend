using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyWizardFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccommodationType",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AreaSqm",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Beds",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CheckInFrom",
                table: "Properties",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CheckOutBy",
                table: "Properties",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExtraGuestFeePerNight",
                table: "Properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListingTitle",
                table: "Properties",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartiesPolicy",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PetsPolicy",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublishedOnSite",
                table: "Properties",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursFrom",
                table: "Properties",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursTo",
                table: "Properties",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmokingPolicy",
                table: "Properties",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccommodationType",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "AreaSqm",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Beds",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CheckInFrom",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CheckOutBy",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ExtraGuestFeePerNight",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ListingTitle",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PartiesPolicy",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PetsPolicy",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PublishedOnSite",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "QuietHoursFrom",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "QuietHoursTo",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "SmokingPolicy",
                table: "Properties");
        }
    }
}
