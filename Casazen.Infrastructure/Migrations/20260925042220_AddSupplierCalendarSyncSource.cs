using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SU-15 (A4-11, A9-14): <c>SupplierAvailability.Source</c> (0 manual, 1 iCal feed) and
    /// <c>SupplierProfiles.CalendarSyncStatus</c> (0 none, 1 syncing, 2 success, 3 failure). Existing days all become
    /// manual: before SU-15 nothing recorded whether a day came from the feed or from the supplier, and freeing a closure
    /// the supplier decided would be worse than keeping a stale busy day (runbook ical.md, "Supplier calendars"). The sync
    /// state of the profiles with an iCal URL is filled from the stored error and last sync.
    /// </summary>
    public partial class AddSupplierCalendarSyncSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalendarSyncStatus",
                table: "SupplierProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "SupplierAvailability",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // CalendarSyncType 2 = ICalFeed. Failure (3) when an error is stored, Success (2) after a sync, otherwise None.
            migrationBuilder.Sql("""
                UPDATE "SupplierProfiles"
                SET "CalendarSyncStatus" = CASE
                        WHEN "CalendarSyncError" IS NOT NULL AND btrim("CalendarSyncError") <> '' THEN 3
                        WHEN "CalendarLastSyncAt" IS NOT NULL THEN 2
                        ELSE 0
                    END
                WHERE "CalendarSyncType" = 2
                  AND "IcalFeedUrl" IS NOT NULL
                  AND btrim("IcalFeedUrl") <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarSyncStatus",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SupplierAvailability");
        }
    }
}
