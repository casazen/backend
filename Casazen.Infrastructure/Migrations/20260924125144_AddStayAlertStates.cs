using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStayAlertStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutReminderJobId",
                table: "Bookings");

            migrationBuilder.CreateTable(
                name: "StayAlertStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ReferenceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    AlertCount = table.Column<int>(type: "integer", nullable: false),
                    LastAlertAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayAlertStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayAlertStates_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StayAlertStates_BookingId_Type",
                table: "StayAlertStates",
                columns: new[] { "BookingId", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StayAlertStates");

            migrationBuilder.AddColumn<string>(
                name: "CheckoutReminderJobId",
                table: "Bookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
