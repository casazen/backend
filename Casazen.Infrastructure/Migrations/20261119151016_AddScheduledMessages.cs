using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoutineId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoutineName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ActionIndex = table.Column<int>(type: "integer", nullable: false),
                    TemplateRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlannedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TextOverrideEncrypted = table.Column<string>(type: "text", nullable: true),
                    BlockedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SkipReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SkippedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SkippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentThreadKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    OutsideHours = table.Column<bool>(type: "boolean", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledMessages", x => x.Id);
                    table.CheckConstraint("CK_ScheduledMessages_ActionIndex", "\"ActionIndex\" >= 0");
                    table.CheckConstraint("CK_ScheduledMessages_Attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("CK_ScheduledMessages_Status", "\"Status\" IN (1, 2, 3, 4, 5, 6, 7)");
                    table.ForeignKey(
                        name: "FK_ScheduledMessages_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduledMessages_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduledMessages_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduledMessages_Routines_RoutineId",
                        column: x => x.RoutineId,
                        principalTable: "Routines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_Open",
                table: "ScheduledMessages",
                columns: new[] { "Status", "PlannedAtUtc" },
                filter: "\"Status\" IN (1, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_OrgId_BookingId",
                table: "ScheduledMessages",
                columns: new[] { "OrgId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_OrgId_PropertyId_PlannedAtUtc",
                table: "ScheduledMessages",
                columns: new[] { "OrgId", "PropertyId", "PlannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_PropertyId",
                table: "ScheduledMessages",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_RoutineId",
                table: "ScheduledMessages",
                column: "RoutineId");

            migrationBuilder.CreateIndex(
                name: "UIX_ScheduledMessages_Booking_Routine_Action",
                table: "ScheduledMessages",
                columns: new[] { "BookingId", "RoutineId", "ActionIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledMessages");
        }
    }
}
