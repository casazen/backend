using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// BK-02, BK-07 (PO 2026-10-08): adds the two columns that store the one-time signed cancel token of the
    /// guest self-service cancellation flow on the <c>Bookings</c> table.
    /// <list type="bullet">
    /// <item><c>GuestCancelTokenHash</c>: SHA-256 (hex) of the raw token, never the token itself.</item>
    /// <item><c>GuestCancelTokenExpiresAt</c>: UTC expiry of the token (default 7 days).</item>
    /// </list>
    /// </remarks>
    public partial class AddGuestCancelToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GuestCancelTokenHash",
                table: "Bookings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestCancelTokenExpiresAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuestCancelTokenHash",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "GuestCancelTokenExpiresAt",
                table: "Bookings");
        }
    }
}
