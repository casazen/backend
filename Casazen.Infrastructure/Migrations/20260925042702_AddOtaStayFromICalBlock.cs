using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOtaStayFromICalBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BookingId",
                table: "CalendarBlocks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChannelLabel",
                table: "Bookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ICalFeedId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OtaReviewRaisedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OtaReviewReason",
                table: "Bookings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_BookingId",
                table: "CalendarBlocks",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ICalFeedId_ExternalId",
                table: "Bookings",
                columns: new[] { "ICalFeedId", "ExternalId" });

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarBlocks_Bookings_BookingId",
                table: "CalendarBlocks",
                column: "BookingId",
                principalTable: "Bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarBlocks_Bookings_BookingId",
                table: "CalendarBlocks");

            migrationBuilder.DropIndex(
                name: "IX_CalendarBlocks_BookingId",
                table: "CalendarBlocks");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_ICalFeedId_ExternalId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BookingId",
                table: "CalendarBlocks");

            migrationBuilder.DropColumn(
                name: "ChannelLabel",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ICalFeedId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaReviewRaisedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OtaReviewReason",
                table: "Bookings");
        }
    }
}
