using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateExistingInventoryImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OctorateInventoryImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKind = table.Column<int>(type: "integer", nullable: false),
                    ScopeGroup = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ScopePropertyIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateInventoryImportBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryImportBatches_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OctorateInventoryIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scenario = table.Column<int>(type: "integer", nullable: false),
                    WizardBatch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateInventoryIntents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryIntents_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryIntents_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OctorateInventoryImportChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    AccessState = table.Column<int>(type: "integer", nullable: false),
                    PanelStepId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccessStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AccessCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AccessConfirmedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RoomsImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateInventoryImportChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryImportChannels_OctorateInventoryImportBatc~",
                        column: x => x.BatchId,
                        principalTable: "OctorateInventoryImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryImportChannels_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OctorateInventoryImportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    OctorateAccommodationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OctorateConnectionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoomsImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RoomCount = table.Column<int>(type: "integer", nullable: false),
                    OctorateReservationsImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReservationsImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CalendarImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeftForLaterAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeftForLaterByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateInventoryImportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryImportItems_OctorateInventoryImportChannel~",
                        column: x => x.ChannelId,
                        principalTable: "OctorateInventoryImportChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OctorateInventoryImportItems_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OctorateInventoryImportBatches_OrgId_CreatedAt",
                table: "OctorateInventoryImportBatches",
                columns: new[] { "OrgId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OctorateInventoryImportChannels_OrgId_ChannelCode",
                table: "OctorateInventoryImportChannels",
                columns: new[] { "OrgId", "ChannelCode" });

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateInventoryImportChannels_BatchId_ChannelCode",
                table: "OctorateInventoryImportChannels",
                columns: new[] { "BatchId", "ChannelCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OctorateInventoryImportItems_OrgId_ConnectionId",
                table: "OctorateInventoryImportItems",
                columns: new[] { "OrgId", "OctorateConnectionId" });

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateInventoryImportItems_ChannelId_ConnectionId",
                table: "OctorateInventoryImportItems",
                columns: new[] { "ChannelId", "OctorateConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OctorateInventoryIntents_PropertyId",
                table: "OctorateInventoryIntents",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateInventoryIntents_OrgId_Group_OrgLevel",
                table: "OctorateInventoryIntents",
                columns: new[] { "OrgId", "WizardBatch" },
                unique: true,
                filter: "\"PropertyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateInventoryIntents_OrgId_PropertyId",
                table: "OctorateInventoryIntents",
                columns: new[] { "OrgId", "PropertyId" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OctorateInventoryImportItems");

            migrationBuilder.DropTable(
                name: "OctorateInventoryIntents");

            migrationBuilder.DropTable(
                name: "OctorateInventoryImportChannels");

            migrationBuilder.DropTable(
                name: "OctorateInventoryImportBatches");
        }
    }
}
