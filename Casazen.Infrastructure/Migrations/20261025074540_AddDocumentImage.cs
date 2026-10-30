using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BiometricAttempts",
                table: "GuestCheckInSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BiometricOutcome",
                table: "GuestCheckInSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    FrontImageUrl = table.Column<string>(type: "text", nullable: false),
                    BackImageUrl = table.Column<string>(type: "text", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentImages_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentImages_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentImages_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentImages_BookingId",
                table: "DocumentImages",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentImages_GuestId",
                table: "DocumentImages",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentImages_OrgId",
                table: "DocumentImages",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentImages");

            migrationBuilder.DropColumn(
                name: "BiometricAttempts",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "BiometricOutcome",
                table: "GuestCheckInSessions");
        }
    }
}
