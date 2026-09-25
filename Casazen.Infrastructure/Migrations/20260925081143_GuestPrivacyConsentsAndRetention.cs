using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GuestPrivacyConsentsAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataRetentionUntil",
                table: "Guests");

            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAt",
                table: "StayGuests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AlloggiatiDataErasedAt",
                table: "Guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuestConsentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestConsentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestConsentRecords_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestConsentRecords_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GuestPrivacyAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StayGuestsAnonymized = table.Column<int>(type: "integer", nullable: false),
                    FilesDeleted = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestPrivacyAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestPrivacyAuditEntries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestConsentRecords_GuestId_Purpose_RecordedAt",
                table: "GuestConsentRecords",
                columns: new[] { "GuestId", "Purpose", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestConsentRecords_OrgId",
                table: "GuestConsentRecords",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestPrivacyAuditEntries_GuestId_OccurredAt",
                table: "GuestPrivacyAuditEntries",
                columns: new[] { "GuestId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestPrivacyAuditEntries_OrgId",
                table: "GuestPrivacyAuditEntries",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuestConsentRecords");

            migrationBuilder.DropTable(
                name: "GuestPrivacyAuditEntries");

            migrationBuilder.DropColumn(
                name: "AnonymizedAt",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDataErasedAt",
                table: "Guests");

            migrationBuilder.AddColumn<DateTime>(
                name: "DataRetentionUntil",
                table: "Guests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
