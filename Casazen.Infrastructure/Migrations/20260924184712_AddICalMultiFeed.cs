using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddICalMultiFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyICalExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExportToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyICalExports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyICalExports_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyICalExports_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalExports_ExportToken",
                table: "PropertyICalExports",
                column: "ExportToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalExports_OrgId",
                table: "PropertyICalExports",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalExports_PropertyId",
                table: "PropertyICalExports",
                column: "PropertyId",
                unique: true);

            // PC-11 (A2-11): the export token of each property moves, unchanged, to its own table, so the export
            // links already pasted on Airbnb/Booking.com keep working. Every existing feed row has one (unique per
            // property until now), also the rows created only for the export link.
            migrationBuilder.Sql(@"
INSERT INTO ""PropertyICalExports"" (""Id"", ""PropertyId"", ""OrgId"", ""ExportToken"", ""CreatedAt"")
SELECT f.""Id"", f.""PropertyId"", f.""OrgId"", f.""ExportToken"", now()
FROM ""PropertyICalFeeds"" f
WHERE NOT EXISTS (SELECT 1 FROM ""PropertyICalExports"" e WHERE e.""PropertyId"" = f.""PropertyId"");
");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "PropertyICalFeeds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PropertyICalFeeds",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "PropertyICalFeeds",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FeedId",
                table: "CalendarBlocks",
                type: "uuid",
                nullable: true);

            // PC-11 (A2-11): the single feed of each property becomes its first feed of the list.
            // - Channel from the host of the URL (still in clear here; encrypted at the next startup by
            //   PropertyICalFeedUrlEncryption): airbnb.<tld> -> Airbnb (1), booking.com -> BookingCom (2), else Other (0).
            //   Same rule as PropertyICalSyncService.InferChannel.
            // - Its imported blocks (Source = ICalImport = 0) are attached to it: nothing is lost, and the next sync
            //   updates them in place.
            // - Rows without URL existed only for the export link (moved above): removed when no block points to them.
            // Imported blocks of a property without any feed row keep FeedId NULL (counted in the notice): they are
            // still occupancy, no sync touches them (docs/runbooks/ical.md).
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_airbnb        bigint;
    v_booking       bigint;
    v_blocks        bigint;
    v_export_only   bigint;
    v_orphan_blocks bigint;
BEGIN
    UPDATE ""PropertyICalFeeds"" SET ""CreatedAt"" = COALESCE(""LastImportAt"", now());

    UPDATE ""PropertyICalFeeds""
    SET ""Channel"" = 1
    WHERE lower(substring(""ImportUrl"" from '^[A-Za-z][A-Za-z0-9+.-]*://([^/:?#]+)')) ~ '(^|\.)airbnb\.[a-z]{2,}(\.[a-z]{2,})?$';
    GET DIAGNOSTICS v_airbnb = ROW_COUNT;

    UPDATE ""PropertyICalFeeds""
    SET ""Channel"" = 2
    WHERE lower(substring(""ImportUrl"" from '^[A-Za-z][A-Za-z0-9+.-]*://([^/:?#]+)')) ~ '(^|\.)booking\.com$';
    GET DIAGNOSTICS v_booking = ROW_COUNT;

    UPDATE ""CalendarBlocks"" b
    SET ""FeedId"" = f.""Id""
    FROM ""PropertyICalFeeds"" f
    WHERE f.""PropertyId"" = b.""PropertyId"" AND b.""Source"" = 0 AND b.""FeedId"" IS NULL;
    GET DIAGNOSTICS v_blocks = ROW_COUNT;

    DELETE FROM ""PropertyICalFeeds"" f
    WHERE (f.""ImportUrl"" IS NULL OR btrim(f.""ImportUrl"") = '')
      AND NOT EXISTS (SELECT 1 FROM ""CalendarBlocks"" b WHERE b.""FeedId"" = f.""Id"");
    GET DIAGNOSTICS v_export_only = ROW_COUNT;

    SELECT count(*) INTO v_orphan_blocks FROM ""CalendarBlocks"" WHERE ""Source"" = 0 AND ""FeedId"" IS NULL;

    RAISE NOTICE 'AddICalMultiFeed: airbnb_feeds=%, booking_feeds=%, blocks_attached=%, export_only_rows_removed=%, imported_blocks_without_feed=%',
        v_airbnb, v_booking, v_blocks, v_export_only, v_orphan_blocks;
END $$;
");

            migrationBuilder.DropIndex(
                name: "IX_PropertyICalFeeds_ExportToken",
                table: "PropertyICalFeeds");

            migrationBuilder.DropColumn(
                name: "ExportToken",
                table: "PropertyICalFeeds");

            migrationBuilder.DropIndex(
                name: "IX_PropertyICalFeeds_PropertyId",
                table: "PropertyICalFeeds");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_PropertyId",
                table: "PropertyICalFeeds",
                column: "PropertyId");

            migrationBuilder.AlterColumn<string>(
                name: "ImportUrl",
                table: "PropertyICalFeeds",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_CalendarBlocks_PropertyId_ExternalUid",
                table: "CalendarBlocks");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_FeedId_ExternalUid",
                table: "CalendarBlocks",
                columns: new[] { "FeedId", "ExternalUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_PropertyId",
                table: "CalendarBlocks",
                column: "PropertyId");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarBlocks_PropertyICalFeeds_FeedId",
                table: "CalendarBlocks",
                column: "FeedId",
                principalTable: "PropertyICalFeeds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one feed per property: the oldest feed of each property stays (with the export token of the
            // property), the other feeds and their blocks are removed. The URLs stay encrypted: the code before PC-11
            // cannot read them and marks those feeds as failed until the host saves the URL again
            // (docs/runbooks/ical.md, "Rollback").
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarBlocks_PropertyICalFeeds_FeedId",
                table: "CalendarBlocks");

            migrationBuilder.DropIndex(
                name: "IX_CalendarBlocks_FeedId_ExternalUid",
                table: "CalendarBlocks");

            migrationBuilder.DropIndex(
                name: "IX_CalendarBlocks_PropertyId",
                table: "CalendarBlocks");

            migrationBuilder.DropIndex(
                name: "IX_PropertyICalFeeds_PropertyId",
                table: "PropertyICalFeeds");

            migrationBuilder.AddColumn<Guid>(
                name: "ExportToken",
                table: "PropertyICalFeeds",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(@"
DELETE FROM ""CalendarBlocks"" b
USING ""PropertyICalFeeds"" f
WHERE b.""FeedId"" = f.""Id""
  AND f.""Id"" <> (SELECT k.""Id"" FROM ""PropertyICalFeeds"" k WHERE k.""PropertyId"" = f.""PropertyId""
                   ORDER BY k.""CreatedAt"", k.""Id"" LIMIT 1);

DELETE FROM ""PropertyICalFeeds"" f
WHERE f.""Id"" <> (SELECT k.""Id"" FROM ""PropertyICalFeeds"" k WHERE k.""PropertyId"" = f.""PropertyId""
                   ORDER BY k.""CreatedAt"", k.""Id"" LIMIT 1);

UPDATE ""PropertyICalFeeds"" f
SET ""ExportToken"" = e.""ExportToken""
FROM ""PropertyICalExports"" e
WHERE e.""PropertyId"" = f.""PropertyId"";

INSERT INTO ""PropertyICalFeeds"" (""Id"", ""PropertyId"", ""OrgId"", ""ExportToken"", ""Channel"", ""CreatedAt"")
SELECT e.""Id"", e.""PropertyId"", e.""OrgId"", e.""ExportToken"", 0, e.""CreatedAt""
FROM ""PropertyICalExports"" e
WHERE NOT EXISTS (SELECT 1 FROM ""PropertyICalFeeds"" f WHERE f.""PropertyId"" = e.""PropertyId"");

UPDATE ""PropertyICalFeeds"" SET ""ExportToken"" = gen_random_uuid() WHERE ""ExportToken"" IS NULL;

UPDATE ""PropertyICalFeeds"" SET ""ImportUrl"" = NULL WHERE length(""ImportUrl"") > 2048;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "ExportToken",
                table: "PropertyICalFeeds",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropTable(
                name: "PropertyICalExports");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "PropertyICalFeeds");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PropertyICalFeeds");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "PropertyICalFeeds");

            migrationBuilder.DropColumn(
                name: "FeedId",
                table: "CalendarBlocks");

            migrationBuilder.AlterColumn<string>(
                name: "ImportUrl",
                table: "PropertyICalFeeds",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_ExportToken",
                table: "PropertyICalFeeds",
                column: "ExportToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyICalFeeds_PropertyId",
                table: "PropertyICalFeeds",
                column: "PropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarBlocks_PropertyId_ExternalUid",
                table: "CalendarBlocks",
                columns: new[] { "PropertyId", "ExternalUid" },
                unique: true);
        }
    }
}
