using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceSendersAndAlloggiatiTransmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountKey",
                table: "PropertyQuesturaCredentials",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<DateTime>(
                name: "AccreditationAttestedAt",
                table: "PropertyQuesturaCredentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccreditationAttestedByUserId",
                table: "PropertyQuesturaCredentials",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApartmentId",
                table: "PropertyQuesturaCredentials",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CredentialVersion",
                table: "PropertyQuesturaCredentials",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "LastVerificationCode",
                table: "PropertyQuesturaCredentials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastVerificationOk",
                table: "PropertyQuesturaCredentials",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerifiedAt",
                table: "PropertyQuesturaCredentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Profile",
                table: "PropertyQuesturaCredentials",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "QuesturaOfficeName",
                table: "PropertyQuesturaCredentials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AlloggiatiDelegationGrantedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlloggiatiDelegationGrantedByUserId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AlloggiatiDelegationRevokedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlloggiatiDelegationRevokedByUserId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlloggiatiDelegationTextVersion",
                table: "Orgs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceiptId",
                table: "AlloggiatiWebReports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlloggiatiReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlloggiatiReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlloggiatiReceipts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PropertyComplianceSenders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlloggiatiSender = table.Column<int>(type: "integer", nullable: false),
                    IstatSender = table.Column<int>(type: "integer", nullable: false),
                    LastChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastChangedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyComplianceSenders", x => x.Id);
                    table.CheckConstraint("CK_PropertyComplianceSenders_IstatFollowsAlloggiati", "\"IstatSender\" = \"AlloggiatiSender\"");
                    table.ForeignKey(
                        name: "FK_PropertyComplianceSenders_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyComplianceSenders_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlloggiatiSendAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountKey = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialVersion = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    GuestCount = table.Column<int>(type: "integer", nullable: false),
                    GuestsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlloggiatiSendAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlloggiatiSendAttempts_AlloggiatiReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "AlloggiatiReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AlloggiatiSendAttempts_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlloggiatiSendAttempts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyQuesturaCredentials_OrgId_AccountKey",
                table: "PropertyQuesturaCredentials",
                columns: new[] { "OrgId", "AccountKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_ReceiptId",
                table: "AlloggiatiWebReports",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiReceipts_OrgId_ReceiptDate",
                table: "AlloggiatiReceipts",
                columns: new[] { "OrgId", "ReceiptDate" });

            migrationBuilder.CreateIndex(
                name: "UIX_AlloggiatiReceipts_OrgId_AccountKey_ReceiptDate_Mode",
                table: "AlloggiatiReceipts",
                columns: new[] { "OrgId", "AccountKey", "ReceiptDate", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiSendAttempts_OrgId_BookingId",
                table: "AlloggiatiSendAttempts",
                columns: new[] { "OrgId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiSendAttempts_ReceiptId",
                table: "AlloggiatiSendAttempts",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "UIX_AlloggiatiSendAttempts_OpenPerBooking",
                table: "AlloggiatiSendAttempts",
                column: "BookingId",
                unique: true,
                filter: "\"Status\" IN (1, 3, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyComplianceSenders_OrgId",
                table: "PropertyComplianceSenders",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyComplianceSenders_PropertyId",
                table: "PropertyComplianceSenders",
                column: "PropertyId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AlloggiatiWebReports_AlloggiatiReceipts_ReceiptId",
                table: "AlloggiatiWebReports",
                column: "ReceiptId",
                principalTable: "AlloggiatiReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlloggiatiWebReports_AlloggiatiReceipts_ReceiptId",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropTable(
                name: "AlloggiatiSendAttempts");

            migrationBuilder.DropTable(
                name: "PropertyComplianceSenders");

            migrationBuilder.DropTable(
                name: "AlloggiatiReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PropertyQuesturaCredentials_OrgId_AccountKey",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropIndex(
                name: "IX_AlloggiatiWebReports_ReceiptId",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropColumn(
                name: "AccountKey",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "AccreditationAttestedAt",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "AccreditationAttestedByUserId",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "ApartmentId",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "CredentialVersion",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "LastVerificationCode",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "LastVerificationOk",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "Profile",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "QuesturaOfficeName",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDelegationGrantedAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDelegationGrantedByUserId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDelegationRevokedAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDelegationRevokedByUserId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "AlloggiatiDelegationTextVersion",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "ReceiptId",
                table: "AlloggiatiWebReports");
        }
    }
}
