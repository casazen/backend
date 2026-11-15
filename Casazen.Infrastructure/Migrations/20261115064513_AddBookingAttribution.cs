using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// E27/F01/US01/T01 (NF-123). Four nullable columns on <c>Bookings</c> (where the guest came from) and one correction of the data that exists: the
    /// reservations of the booking engine of the agency that the import made before the column existed are attributed to it
    /// (<c>octorate-booking-engine</c>, the value <c>BookingAttribution.OctorateBookingEngineSource</c> says), so the host reads them as «the site of the
    /// agency», not as CasaZen's own. They are the bookings of the booking site (<c>Source</c> 0, <c>BookingSource.Direct</c>) that have a reservation on the
    /// channel manager and no row of the mirror (<c>OctorateBookingSyncs</c>): CasaZen makes that row before it creates a reservation, so a reservation with
    /// none is one CasaZen did not make (the definition <c>HostBookingService</c> uses to leave such a booking to the channel manager). A booking that has an
    /// attribution already keeps it; nothing else changes, not even <c>UpdatedAt</c>.
    /// </summary>
    public partial class AddBookingAttribution : Migration
    {
        public const string BackfillSql = """
            UPDATE "Bookings" AS b
            SET "AttributionSource" = 'octorate-booking-engine'
            WHERE b."Source" = 0
              AND b."OctorateReservationId" IS NOT NULL
              AND btrim(b."OctorateReservationId") <> ''
              AND b."AttributionSource" IS NULL
              AND NOT EXISTS (SELECT 1 FROM "OctorateBookingSyncs" AS s WHERE s."BookingId" = b."Id");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributionCampaign",
                table: "Bookings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionMedium",
                table: "Bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionReferrerHost",
                table: "Bookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionSource",
                table: "Bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributionCampaign",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionMedium",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionReferrerHost",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AttributionSource",
                table: "Bookings");
        }
    }
}
