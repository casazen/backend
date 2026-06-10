using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAlloggiatiCheckInMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentScanUrl",
                table: "Guests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckInToken",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManuallyCompleted",
                table: "AlloggiatiWebReports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PropertyQuesturaCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WsKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyQuesturaCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyQuesturaCredentials_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings",
                column: "CheckInToken",
                unique: true,
                filter: "\"CheckInToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyQuesturaCredentials_PropertyId",
                table: "PropertyQuesturaCredentials",
                column: "PropertyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyQuesturaCredentials");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DocumentScanUrl",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "CheckInToken",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ManuallyCompleted",
                table: "AlloggiatiWebReports");
        }
    }
}
