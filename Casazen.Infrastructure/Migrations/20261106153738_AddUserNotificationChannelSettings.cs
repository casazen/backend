using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotificationChannelSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationEmailSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ThreadKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastEmailAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEmailSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationEmailSlots_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationEmailSlots_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationChannelSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channels = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationChannelSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotificationChannelSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserNotificationChannelSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEmailSlots_UserId",
                table: "NotificationEmailSlots",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UIX_NotificationEmailSlots_OrgId_UserId_ThreadKey",
                table: "NotificationEmailSlots",
                columns: new[] { "OrgId", "UserId", "ThreadKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationChannelSettings_OrgId_UserId",
                table: "UserNotificationChannelSettings",
                columns: new[] { "OrgId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationChannelSettings_UserId",
                table: "UserNotificationChannelSettings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UIX_UserNotificationChannelSettings_OrgId_UserId_EventType",
                table: "UserNotificationChannelSettings",
                columns: new[] { "OrgId", "UserId", "EventType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationEmailSlots");

            migrationBuilder.DropTable(
                name: "UserNotificationChannelSettings");
        }
    }
}
