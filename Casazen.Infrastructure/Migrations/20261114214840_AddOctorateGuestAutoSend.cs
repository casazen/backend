using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateGuestAutoSend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorFields",
                table: "StayGuestOctorateLinks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoSendGuestsToOctorate",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoSendGuestsToOctorateChangedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoSendGuestsToOctorateChangedByUserId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorFields",
                table: "StayGuestOctorateLinks");

            migrationBuilder.DropColumn(
                name: "AutoSendGuestsToOctorate",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AutoSendGuestsToOctorateChangedAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AutoSendGuestsToOctorateChangedByUserId",
                table: "Orgs");
        }
    }
}
