using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateMappingAndOAuthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OctorateListingMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OctorateAccommodationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SnapshotAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SnapshotCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SnapshotMaxGuests = table.Column<int>(type: "integer", nullable: true),
                    SnapshotRoomsJson = table.Column<string>(type: "jsonb", nullable: false),
                    OctorateProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MatchScore = table.Column<int>(type: "integer", nullable: false),
                    CandidatePropertyIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChannelIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateListingMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateListingMappings_OctorateConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "OctorateConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OctorateListingMappings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OctorateOAuthStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StateHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateOAuthStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OctorateListingMappings_ConnectionId",
                table: "OctorateListingMappings",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_OctorateListingMappings_OctorateAccommodationId",
                table: "OctorateListingMappings",
                column: "OctorateAccommodationId");

            migrationBuilder.CreateIndex(
                name: "IX_OctorateListingMappings_OrgId_Status",
                table: "OctorateListingMappings",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OctorateListingMappings_PropertyId",
                table: "OctorateListingMappings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateListingMappings_OrgId_ConnectionId_Accommodation",
                table: "OctorateListingMappings",
                columns: new[] { "OrgId", "ConnectionId", "OctorateAccommodationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateListingMappings_OrgId_PropertyId_Confirmed",
                table: "OctorateListingMappings",
                columns: new[] { "OrgId", "PropertyId" },
                unique: true,
                filter: "\"Status\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_OctorateOAuthStates_ExpiresAt",
                table: "OctorateOAuthStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateOAuthStates_StateHash",
                table: "OctorateOAuthStates",
                column: "StateHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OctorateListingMappings");

            migrationBuilder.DropTable(
                name: "OctorateOAuthStates");
        }
    }
}
