using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Open service requests without a start/end get Europe/Rome 10:00 on the stay check-out day (or on CreatedAt's Rome
    /// day) and a duration from the catalog listing or 120 minutes. Untimed requests are no longer allowed; this adapts
    /// rows that already exist so they stop occupying a whole day by omission.
    /// </summary>
    public partial class BackfillTimedServiceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "ServiceRequests" AS sr
                SET
                    "ScheduledStartUtc" = start_at,
                    "ScheduledEndUtc" = start_at + make_interval(mins => duration_mins),
                    "UpdatedAt" = NOW() AT TIME ZONE 'utc'
                FROM (
                    SELECT
                        sr2."Id",
                        (
                            date_trunc(
                                'day',
                                COALESCE(b."CheckOutDate", sr2."CreatedAt") AT TIME ZONE 'Europe/Rome')
                            + interval '10 hours'
                        ) AT TIME ZONE 'Europe/Rome' AS start_at,
                        COALESCE(
                            NULLIF(listing."DurationMinutes", 0),
                            120
                        ) AS duration_mins
                    FROM "ServiceRequests" AS sr2
                    LEFT JOIN "Bookings" AS b ON b."Id" = sr2."BookingId"
                    LEFT JOIN "SupplierServiceListings" AS listing ON listing."Id" = sr2."ServiceListingId"
                    WHERE sr2."ScheduledStartUtc" IS NULL
                      AND sr2."Status" IN (0, 1, 2) -- Richiesto, PresoInCarico, InCorso
                ) AS timed
                WHERE sr."Id" = timed."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nulling times would recreate the forbidden untimed shape.
        }
    }
}
