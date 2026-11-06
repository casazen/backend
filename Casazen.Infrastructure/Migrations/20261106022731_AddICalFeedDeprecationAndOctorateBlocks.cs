using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddICalFeedDeprecationAndOctorateBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Deprecated",
                table: "PropertyICalFeeds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeprecatedAt",
                table: "PropertyICalFeeds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_CalendarBlocks_PropertyId_ExternalUid_Octorate",
                table: "CalendarBlocks",
                columns: new[] { "PropertyId", "ExternalUid" },
                unique: true,
                filter: "\"Source\" = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_CalendarBlocks_PropertyId_ExternalUid_Octorate",
                table: "CalendarBlocks");

            migrationBuilder.DropColumn(
                name: "Deprecated",
                table: "PropertyICalFeeds");

            migrationBuilder.DropColumn(
                name: "DeprecatedAt",
                table: "PropertyICalFeeds");
        }
    }
}
