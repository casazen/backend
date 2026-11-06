using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckInGuestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentExpiresOn",
                table: "StayGuests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentIssuedOn",
                table: "StayGuests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EducationLevel",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceComuneCode",
                table: "StayGuests",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceComuneName",
                table: "StayGuests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCountryCode",
                table: "StayGuests",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCountryName",
                table: "StayGuests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResidenceInItaly",
                table: "StayGuests",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceProvince",
                table: "StayGuests",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemptionCode",
                table: "StayGuests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TourismType",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TravelWay",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "GuestCheckInSessions",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoShowAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentExpiresOn",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "DocumentIssuedOn",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "EducationLevel",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceComuneCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceComuneName",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceCountryCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceCountryName",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceInItaly",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceProvince",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TaxExemptionCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TourismType",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TravelWay",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "NoShowAt",
                table: "Bookings");
        }
    }
}
