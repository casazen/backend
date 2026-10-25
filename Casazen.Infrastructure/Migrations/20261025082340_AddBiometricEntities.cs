using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBiometricEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BiometricSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptThreshold = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    EscalateThreshold = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiometricSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BiometricVerificationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ThresholdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    OperatorUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OperatorNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricVerificationLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiometricVerificationLogs_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BiometricVerificationLogs_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BiometricVerificationLogs_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BiometricVerificationLogs_Users_OperatorUserId",
                        column: x => x.OperatorUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiometricSettings_OrgId",
                table: "BiometricSettings",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricVerificationLogs_BookingId",
                table: "BiometricVerificationLogs",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricVerificationLogs_GuestId",
                table: "BiometricVerificationLogs",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricVerificationLogs_OperatorUserId",
                table: "BiometricVerificationLogs",
                column: "OperatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricVerificationLogs_OrgId",
                table: "BiometricVerificationLogs",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BiometricSettings");

            migrationBuilder.DropTable(
                name: "BiometricVerificationLogs");
        }
    }
}
