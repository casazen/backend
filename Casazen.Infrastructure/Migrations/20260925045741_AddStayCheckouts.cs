using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStayCheckouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StayCheckouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentStep = table.Column<int>(type: "integer", nullable: false),
                    DepartureConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    CleaningChoice = table.Column<int>(type: "integer", nullable: true),
                    CleaningSupplierOrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    CleaningCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CleaningNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CleaningRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    TouristTaxCollection = table.Column<int>(type: "integer", nullable: true),
                    PropertyReady = table.Column<bool>(type: "boolean", nullable: true),
                    PropertyNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PropertyReadyAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayCheckouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayCheckouts_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayCheckouts_ServiceRequests_CleaningRequestId",
                        column: x => x.CleaningRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StayCheckouts_BookingId",
                table: "StayCheckouts",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayCheckouts_CleaningRequestId",
                table: "StayCheckouts",
                column: "CleaningRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StayCheckouts");
        }
    }
}
