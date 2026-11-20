using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingFlowSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingFlowSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckInLinkMode = table.Column<int>(type: "integer", nullable: false),
                    CheckInLinkDaysBefore = table.Column<int>(type: "integer", nullable: false),
                    ArrivalMode = table.Column<int>(type: "integer", nullable: false),
                    DepartureMode = table.Column<int>(type: "integer", nullable: false),
                    PropertyReadyMode = table.Column<int>(type: "integer", nullable: false),
                    PropertyReadyTrigger = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingFlowSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingFlowSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingFlowSettings_OrgId",
                table: "BookingFlowSettings",
                column: "OrgId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingFlowSettings");
        }
    }
}
