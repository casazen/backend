using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerPortalOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InboxEmailOptOut",
                table: "OwnerUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OwnerConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerStayRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerStatementVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedByOwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerConversations_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerConversations_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerDocuments_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDocuments_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDocuments_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerPortalContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvitationExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvitedByOwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerPortalContacts", x => x.Id);
                    table.CheckConstraint("CK_OwnerPortalContacts_Permissions", "\"Permissions\" > 0");
                    table.ForeignKey(
                        name: "FK_OwnerPortalContacts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerPortalContacts_OwnerUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "OwnerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerPortalContacts_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerStayRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByOwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AutoApprove = table.Column<bool>(type: "boolean", nullable: false),
                    CalendarBlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlockChangeProcessId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncState = table.Column<int>(type: "integer", nullable: false),
                    BlockReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseChangeProcessId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseSyncState = table.Column<int>(type: "integer", nullable: false),
                    SyncErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SyncCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerStayRequests", x => x.Id);
                    table.CheckConstraint("CK_OwnerStayRequests_Dates", "\"EndDate\" > \"StartDate\" AND \"Nights\" >= 1");
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerStayRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    YearlyNightAllowance = table.Column<int>(type: "integer", nullable: true),
                    MinAdvanceDays = table.Column<int>(type: "integer", nullable: false),
                    MaxAdvanceDays = table.Column<int>(type: "integer", nullable: true),
                    MinNights = table.Column<int>(type: "integer", nullable: false),
                    MaxNights = table.Column<int>(type: "integer", nullable: true),
                    ApprovalRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ActiveUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    BlackoutsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerStayRules", x => x.Id);
                    table.CheckConstraint("CK_OwnerStayRules_Advance", "\"MinAdvanceDays\" >= 0 AND (\"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")");
                    table.CheckConstraint("CK_OwnerStayRules_Allowance", "\"YearlyNightAllowance\" IS NULL OR \"YearlyNightAllowance\" BETWEEN 0 AND 366");
                    table.CheckConstraint("CK_OwnerStayRules_Nights", "\"MinNights\" >= 1 AND (\"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\")");
                    table.CheckConstraint("CK_OwnerStayRules_Period", "\"ActiveFrom\" IS NULL OR \"ActiveUntil\" IS NULL OR \"ActiveUntil\" >= \"ActiveFrom\"");
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sender = table.Column<int>(type: "integer", nullable: false),
                    SenderUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SenderOwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AttachmentKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AttachmentFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AttachmentContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AttachmentSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    AttachmentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttachmentScanEngine = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerMessages", x => x.Id);
                    table.CheckConstraint("CK_OwnerMessages_Sender", "(\"SenderUserId\" IS NULL) <> (\"SenderOwnerUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_OwnerMessages_OwnerConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "OwnerConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerDocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ScanEngine = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDocumentVersions", x => x.Id);
                    table.CheckConstraint("CK_OwnerDocumentVersions_Dates", "\"EffectiveFrom\" IS NULL OR \"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_OwnerDocumentVersions_Number", "\"VersionNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_OwnerDocumentVersions_OwnerDocuments_OwnerDocumentId",
                        column: x => x.OwnerDocumentId,
                        principalTable: "OwnerDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerDocumentDownloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerDocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDocumentDownloads", x => x.Id);
                    table.CheckConstraint("CK_OwnerDocumentDownloads_Who", "(\"OwnerUserId\" IS NULL) <> (\"UserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_OwnerDocumentDownloads_OwnerDocumentVersions_OwnerDocumentV~",
                        column: x => x.OwnerDocumentVersionId,
                        principalTable: "OwnerDocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDocumentDownloads_OwnerDocuments_OwnerDocumentId",
                        column: x => x.OwnerDocumentId,
                        principalTable: "OwnerDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerConversations_OrgId_PropertyOwnerId_LastMessageAt",
                table: "OwnerConversations",
                columns: new[] { "OrgId", "PropertyOwnerId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerConversations_PropertyOwnerId",
                table: "OwnerConversations",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocumentDownloads_OwnerDocumentId_DownloadedAt",
                table: "OwnerDocumentDownloads",
                columns: new[] { "OwnerDocumentId", "DownloadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocumentDownloads_OwnerDocumentVersionId",
                table: "OwnerDocumentDownloads",
                column: "OwnerDocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocuments_OrgId_PropertyOwnerId",
                table: "OwnerDocuments",
                columns: new[] { "OrgId", "PropertyOwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocuments_PropertyId",
                table: "OwnerDocuments",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocuments_PropertyOwnerId",
                table: "OwnerDocuments",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocumentVersions_OrgId_ExpiresOn",
                table: "OwnerDocumentVersions",
                columns: new[] { "OrgId", "ExpiresOn" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDocumentVersions_OwnerDocumentId_VersionNumber",
                table: "OwnerDocumentVersions",
                columns: new[] { "OwnerDocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerMessages_ConversationId_CreatedAt",
                table: "OwnerMessages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPortalContacts_OwnerUserId_Status",
                table: "OwnerPortalContacts",
                columns: new[] { "OwnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPortalContacts_PropertyOwnerId",
                table: "OwnerPortalContacts",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPortalContacts_TokenHash",
                table: "OwnerPortalContacts",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerPortalContacts_Owner_Email_InForce",
                table: "OwnerPortalContacts",
                columns: new[] { "OrgId", "PropertyOwnerId", "Email" },
                unique: true,
                filter: "\"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_CalendarBlockId",
                table: "OwnerStayRequests",
                column: "CalendarBlockId",
                unique: true,
                filter: "\"CalendarBlockId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_OrgId_PropertyOwnerId_StartDate",
                table: "OwnerStayRequests",
                columns: new[] { "OrgId", "PropertyOwnerId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_PropertyId_Status_StartDate",
                table: "OwnerStayRequests",
                columns: new[] { "PropertyId", "Status", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_PropertyOwnerId",
                table: "OwnerStayRequests",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_Status_EndDate",
                table: "OwnerStayRequests",
                columns: new[] { "Status", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRules_OrgId",
                table: "OwnerStayRules",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRules_PropertyOwnerId",
                table: "OwnerStayRules",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStayRules_Property",
                table: "OwnerStayRules",
                column: "PropertyId",
                unique: true,
                filter: "\"PropertyOwnerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStayRules_Property_Owner",
                table: "OwnerStayRules",
                columns: new[] { "PropertyId", "PropertyOwnerId" },
                unique: true,
                filter: "\"PropertyOwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerDocumentDownloads");

            migrationBuilder.DropTable(
                name: "OwnerMessages");

            migrationBuilder.DropTable(
                name: "OwnerPortalContacts");

            migrationBuilder.DropTable(
                name: "OwnerStayRequests");

            migrationBuilder.DropTable(
                name: "OwnerStayRules");

            migrationBuilder.DropTable(
                name: "OwnerDocumentVersions");

            migrationBuilder.DropTable(
                name: "OwnerConversations");

            migrationBuilder.DropTable(
                name: "OwnerDocuments");

            migrationBuilder.DropColumn(
                name: "InboxEmailOptOut",
                table: "OwnerUsers");
        }
    }
}
