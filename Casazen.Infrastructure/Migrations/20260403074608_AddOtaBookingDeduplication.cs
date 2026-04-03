using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOtaBookingDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CancellationPolicyId",
                table: "Properties",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationDate",
                table: "Bookings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "Bookings",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CancellationPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FullRefundHours = table.Column<int>(type: "int", nullable: false),
                    PartialRefundPercent = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PartialRefundHours = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancellationPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OtaSyncLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SyncStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SyncCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    BookingsCreated = table.Column<int>(type: "int", nullable: false),
                    BookingsUpdated = table.Column<int>(type: "int", nullable: false),
                    BookingsCancelled = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", maxLength: 5000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtaSyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OtaSyncLogs_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Properties_CancellationPolicyId",
                table: "Properties",
                column: "CancellationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_PropertyId_ExternalId_Source",
                table: "Bookings",
                columns: new[] { "PropertyId", "ExternalId", "Source" },
                unique: true,
                filter: "[ExternalId] IS NOT NULL AND [ExternalId] != ''");

            migrationBuilder.CreateIndex(
                name: "IX_OtaSyncLogs_PropertyId",
                table: "OtaSyncLogs",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OtaSyncLogs_SyncStartedAt",
                table: "OtaSyncLogs",
                column: "SyncStartedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_CancellationPolicies_CancellationPolicyId",
                table: "Properties",
                column: "CancellationPolicyId",
                principalTable: "CancellationPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Properties_CancellationPolicies_CancellationPolicyId",
                table: "Properties");

            migrationBuilder.DropTable(
                name: "CancellationPolicies");

            migrationBuilder.DropTable(
                name: "OtaSyncLogs");

            migrationBuilder.DropIndex(
                name: "IX_Properties_CancellationPolicyId",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_PropertyId_ExternalId_Source",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CancellationPolicyId",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CancellationDate",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Bookings");
        }
    }
}
