using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHostInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostInviteAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InviteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ActorOrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostInviteAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HostInviteRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InvitedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    InvitedByOrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PersonalDataErasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostInviteRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostInviteAuditEntries_ActorOrgId_OccurredAt",
                table: "HostInviteAuditEntries",
                columns: new[] { "ActorOrgId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HostInviteAuditEntries_InviteId",
                table: "HostInviteAuditEntries",
                column: "InviteId");

            migrationBuilder.CreateIndex(
                name: "IX_HostInviteRecords_InvitedByOrgId_CreatedAt",
                table: "HostInviteRecords",
                columns: new[] { "InvitedByOrgId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HostInviteRecords_InvitedByUserId_Email",
                table: "HostInviteRecords",
                columns: new[] { "InvitedByUserId", "Email" });

            migrationBuilder.CreateIndex(
                name: "UIX_HostInviteRecords_TokenHash",
                table: "HostInviteRecords",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostInviteAuditEntries");

            migrationBuilder.DropTable(
                name: "HostInviteRecords");
        }
    }
}
