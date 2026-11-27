using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOtaMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageTemplates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OtaMessageSyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OctorateAccommodationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialImportCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImportCursor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastCreatedAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRecentPassAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDeepPassAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtaMessageSyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OtaMessageSyncStates_OctorateConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "OctorateConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OtaMessageSyncStates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OtaMessageSyncStates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OtaMessageThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    OctorateThreadId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OctorateAccommodationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OctorateReservationId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ChannelSource = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    GuestDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageDirection = table.Column<int>(type: "integer", nullable: true),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultProcessor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AiAssistantOn = table.Column<bool>(type: "boolean", nullable: true),
                    AnonymizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtaMessageThreads", x => x.Id);
                    table.CheckConstraint("CK_OtaMessageThreads_ChannelSource", "\"ChannelSource\" IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_OtaMessageThreads_Kind", "\"Kind\" IN (1, 2)");
                    table.CheckConstraint("CK_OtaMessageThreads_LastMessageDirection", "\"LastMessageDirection\" IS NULL OR \"LastMessageDirection\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_OtaMessageThreads_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OtaMessageThreads_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OtaMessageThreads_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OtaMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    OctorateMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    SenderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TextEncrypted = table.Column<string>(type: "text", nullable: false),
                    AttachmentUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttachmentContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveryState = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SentByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtaMessages", x => x.Id);
                    table.CheckConstraint("CK_OtaMessages_DeliveryState", "\"DeliveryState\" IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_OtaMessages_Direction", "\"Direction\" IN (1, 2)");
                    table.CheckConstraint("CK_OtaMessages_Origin", "\"Origin\" IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_OtaMessages_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OtaMessages_OtaMessageThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "OtaMessageThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_MessageTemplates_OrgId_Name_Language",
                table: "MessageTemplates",
                columns: new[] { "OrgId", "Name", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessages_ThreadId_SentAt",
                table: "OtaMessages",
                columns: new[] { "ThreadId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "UIX_OtaMessages_OrgId_OctorateMessageId",
                table: "OtaMessages",
                columns: new[] { "OrgId", "OctorateMessageId" },
                unique: true,
                filter: "\"OctorateMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_OtaMessages_ThreadId_IdempotencyKey",
                table: "OtaMessages",
                columns: new[] { "ThreadId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageSyncStates_ConnectionId",
                table: "OtaMessageSyncStates",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageSyncStates_PropertyId",
                table: "OtaMessageSyncStates",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OtaMessageSyncStates_OrgId_OctorateAccommodationId",
                table: "OtaMessageSyncStates",
                columns: new[] { "OrgId", "OctorateAccommodationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageThreads_BookingId",
                table: "OtaMessageThreads",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageThreads_OrgId_BookingId",
                table: "OtaMessageThreads",
                columns: new[] { "OrgId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageThreads_OrgId_OctorateReservationId_Unlinked",
                table: "OtaMessageThreads",
                columns: new[] { "OrgId", "OctorateReservationId" },
                filter: "\"BookingId\" IS NULL AND \"OctorateReservationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageThreads_OrgId_PropertyId_LastMessageAt",
                table: "OtaMessageThreads",
                columns: new[] { "OrgId", "PropertyId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OtaMessageThreads_PropertyId",
                table: "OtaMessageThreads",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OtaMessageThreads_OrgId_OctorateThreadId",
                table: "OtaMessageThreads",
                columns: new[] { "OrgId", "OctorateThreadId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageTemplates");

            migrationBuilder.DropTable(
                name: "OtaMessages");

            migrationBuilder.DropTable(
                name: "OtaMessageSyncStates");

            migrationBuilder.DropTable(
                name: "OtaMessageThreads");
        }
    }
}
