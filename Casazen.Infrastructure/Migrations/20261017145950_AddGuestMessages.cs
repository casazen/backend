using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuestThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastGuestMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHostMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GuestNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestThreads_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestThreads_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuestThreads_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuestMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sender = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AuthorUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestMessages", x => x.Id);
                    table.CheckConstraint("CK_GuestMessages_Sender", "\"Sender\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_GuestMessages_GuestThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "GuestThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestMessages_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestMessages_OrgId",
                table: "GuestMessages",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestMessages_ThreadId_CreatedAt",
                table: "GuestMessages",
                columns: new[] { "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UIX_GuestMessages_ThreadId_IdempotencyKey",
                table: "GuestMessages",
                columns: new[] { "ThreadId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestThreads_OrgId_LastMessageAt",
                table: "GuestThreads",
                columns: new[] { "OrgId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestThreads_PropertyId",
                table: "GuestThreads",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_GuestThreads_BookingId",
                table: "GuestThreads",
                column: "BookingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuestMessages");

            migrationBuilder.DropTable(
                name: "GuestThreads");
        }
    }
}
