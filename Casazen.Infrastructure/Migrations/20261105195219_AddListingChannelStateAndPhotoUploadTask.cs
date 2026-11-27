using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListingChannelStateAndPhotoUploadTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListingChannelStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContentVersion = table.Column<int>(type: "integer", nullable: false),
                    ContentHashesJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExternalChangeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingChannelStates", x => x.Id);
                    table.CheckConstraint("CK_ListingChannelStates_ContentVersion", "\"ContentVersion\" >= 0");
                    table.ForeignKey(
                        name: "FK_ListingChannelStates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ListingChannelStates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhotoUploadTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhotoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TargetRoomType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OctorateQueueId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OctoratePhotoId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoUploadTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoUploadTasks_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoUploadTasks_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingChannelStates_OrgId",
                table: "ListingChannelStates",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_ListingChannelStates_PropertyId_ChannelCode",
                table: "ListingChannelStates",
                columns: new[] { "PropertyId", "ChannelCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoUploadTasks_OrgId",
                table: "PhotoUploadTasks",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PhotoUploadTasks_PropertyId_PhotoUrl",
                table: "PhotoUploadTasks",
                columns: new[] { "PropertyId", "PhotoUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingChannelStates");

            migrationBuilder.DropTable(
                name: "PhotoUploadTasks");
        }
    }
}
