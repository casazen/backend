using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckInLinkDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OtaMessages_Origin",
                table: "OtaMessages");

            migrationBuilder.CreateTable(
                name: "CheckInLinkDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OtaMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContactReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContactRefused = table.Column<bool>(type: "boolean", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckInLinkDeliveries", x => x.Id);
                    table.CheckConstraint("CK_CheckInLinkDeliveries_Channel", "\"Channel\" IN (1, 2, 3)");
                    table.CheckConstraint("CK_CheckInLinkDeliveries_State", "\"State\" IN (1, 2, 3, 4, 5, 6)");
                    table.ForeignKey(
                        name: "FK_CheckInLinkDeliveries_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckInLinkDeliveries_GuestCheckInSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GuestCheckInSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CheckInLinkDeliveries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CheckInLinkDeliveries_OtaMessages_OtaMessageId",
                        column: x => x.OtaMessageId,
                        principalTable: "OtaMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OtaMessages_Origin",
                table: "OtaMessages",
                sql: "\"Origin\" IN (1, 2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_CheckInLinkDeliveries_OrgId_State_Open",
                table: "CheckInLinkDeliveries",
                columns: new[] { "OrgId", "State" },
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CheckInLinkDeliveries_OtaMessageId",
                table: "CheckInLinkDeliveries",
                column: "OtaMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckInLinkDeliveries_SessionId",
                table: "CheckInLinkDeliveries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "UIX_CheckInLinkDeliveries_BookingId",
                table: "CheckInLinkDeliveries",
                column: "BookingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckInLinkDeliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OtaMessages_Origin",
                table: "OtaMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OtaMessages_Origin",
                table: "OtaMessages",
                sql: "\"Origin\" IN (1, 2, 3)");
        }
    }
}
