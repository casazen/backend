using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateBookingSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OctorateRegisteredAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OctorateTransactionId",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HostPaymentArrangement",
                table: "Bookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentLinkSentAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OctorateBookingSyncs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastOperation = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OctorateUpdateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OctorateVersionJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateBookingSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateBookingSyncs_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_Payments_OrgId_OctorateTransactionId",
                table: "Payments",
                columns: new[] { "OrgId", "OctorateTransactionId" },
                unique: true,
                filter: "\"OctorateTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OctorateBookingSyncs_OrgId_State",
                table: "OctorateBookingSyncs",
                columns: new[] { "OrgId", "State" });

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateBookingSyncs_BookingId",
                table: "OctorateBookingSyncs",
                column: "BookingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OctorateBookingSyncs");

            migrationBuilder.DropIndex(
                name: "UIX_Payments_OrgId_OctorateTransactionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OctorateRegisteredAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OctorateTransactionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "HostPaymentArrangement",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaymentLinkSentAt",
                table: "Bookings");
        }
    }
}
