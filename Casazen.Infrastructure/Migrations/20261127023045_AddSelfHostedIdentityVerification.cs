using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfHostedIdentityVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityChannel",
                table: "GuestCheckInSessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityChoice",
                table: "GuestCheckInSessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityChoiceAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityClosedAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityFormatId",
                table: "GuestCheckInSessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityGuestId",
                table: "GuestCheckInSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdentityGuidedRetries",
                table: "GuestCheckInSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IdentityLastAttemptKey",
                table: "GuestCheckInSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityLastAttemptResult",
                table: "GuestCheckInSessions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityNoticeTextHash",
                table: "GuestCheckInSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityNoticeVersion",
                table: "GuestCheckInSessions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityOpenedAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityOutcomeCode",
                table: "GuestCheckInSessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "BiometricVerificationLogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionMode",
                table: "BiometricVerificationLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FormatId",
                table: "BiometricVerificationLogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "BiometricVerificationLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyHash",
                table: "BiometricVerificationLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyVersion",
                table: "BiometricVerificationLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShadowOutcome",
                table: "BiometricVerificationLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityReviewEvidences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckInSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationLogId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletionReason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityReviewEvidences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityReviewEvidences_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdentitySegmentSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    FormatId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RequiredAssurance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "consistency"),
                    QualifiedPolicyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    QualifiedPolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QualifiedWeightsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QualificationReportSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QualifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QualifiedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SuspendedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentitySegmentSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentitySegmentSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentitySegmentSettings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestCheckInSessions_OrgId_IdentityOpenedAt",
                table: "GuestCheckInSessions",
                columns: new[] { "OrgId", "IdentityOpenedAt" });

            migrationBuilder.CreateIndex(
                name: "UIX_BiometricVerificationLogs_BookingId_IdempotencyKey",
                table: "BiometricVerificationLogs",
                columns: new[] { "BookingId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityReviewEvidences_ExpiresAt_Live",
                table: "IdentityReviewEvidences",
                column: "ExpiresAt",
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityReviewEvidences_OrgId_BookingId",
                table: "IdentityReviewEvidences",
                columns: new[] { "OrgId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentitySegmentSettings_PropertyId",
                table: "IdentitySegmentSettings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_IdentitySegmentSettings_OrgId_FormatId_Channel",
                table: "IdentitySegmentSettings",
                columns: new[] { "OrgId", "FormatId", "Channel" },
                unique: true,
                filter: "\"PropertyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_IdentitySegmentSettings_OrgId_PropertyId_FormatId_Channel",
                table: "IdentitySegmentSettings",
                columns: new[] { "OrgId", "PropertyId", "FormatId", "Channel" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityReviewEvidences");

            migrationBuilder.DropTable(
                name: "IdentitySegmentSettings");

            migrationBuilder.DropIndex(
                name: "IX_GuestCheckInSessions_OrgId_IdentityOpenedAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropIndex(
                name: "UIX_BiometricVerificationLogs_BookingId_IdempotencyKey",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "IdentityChannel",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityChoice",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityChoiceAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityClosedAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityFormatId",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityGuestId",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityGuidedRetries",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityLastAttemptKey",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityLastAttemptResult",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityNoticeTextHash",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityNoticeVersion",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityOpenedAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "IdentityOutcomeCode",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "DecisionMode",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "FormatId",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "PolicyHash",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "ShadowOutcome",
                table: "BiometricVerificationLogs");
        }
    }
}
