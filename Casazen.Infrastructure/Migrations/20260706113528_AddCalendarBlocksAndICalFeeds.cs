using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarBlocksAndICalFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExternalUid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarBlocks_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarBlocks_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyICalFeeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ExportToken = table.Column<Guid>(type: "uuid", nullable: false),
                    LastImportAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastImportStatus = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyICalFeeds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyICalFeeds_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyICalFeeds_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_OrgId",
                table: "CalendarBlocks",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_PropertyId_ExternalUid",
                table: "CalendarBlocks",
                columns: new[] { "PropertyId", "ExternalUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_ExportToken",
                table: "PropertyICalFeeds",
                column: "ExportToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_OrgId",
                table: "PropertyICalFeeds",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_PropertyId",
                table: "PropertyICalFeeds",
                column: "PropertyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarBlocks");

            migrationBuilder.DropTable(
                name: "PropertyICalFeeds");
        }
    }
}
