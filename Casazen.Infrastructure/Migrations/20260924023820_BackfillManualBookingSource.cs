using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PC-01 (A2-01): marks the bookings already entered by hosts with the new source <c>BookingSource.Manual</c>
    /// (stored as 8), so they are told apart from the public checkout (<c>Direct</c> = 0).
    /// </summary>
    /// <remarks>
    /// Data only, no schema change (the source is an integer column). Until now both paths stored <c>Direct</c>; a
    /// booking is taken as a host one only when nothing of the public checkout is on it: no free-refund deadline
    /// (always set by the checkout), no Stripe SetupIntent, no payment with a PaymentIntent and a guest that was not
    /// captured by the checkout form. When in doubt it stays <c>Direct</c>. Statuses are not changed: host bookings
    /// left <c>Pending</c> by the old code are confirmed by the host (PC-07). Down puts them back to <c>Direct</c>.
    /// </remarks>
    public partial class BackfillManualBookingSource : Migration
    {
        /// <summary>Backfill of the host bookings. Public so that a PostgreSQL test runs exactly this statement.</summary>
        public const string BackfillSql = """
            UPDATE "Bookings" AS b
            SET "Source" = 8
            WHERE b."Source" = 0
              AND b."FreeRefundDeadline" IS NULL
              AND b."StripeSetupIntentId" IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "Payments" AS p
                  WHERE p."BookingId" = b."Id" AND p."StripePaymentIntentId" IS NOT NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM "Guests" AS g
                  WHERE g."Id" = b."GuestId" AND g."DataProcessingPurpose" = 'Direct Booking Checkout');
            """;

        /// <summary>Reverts the backfill: every manual booking goes back to <c>Direct</c>.</summary>
        public const string RevertSql = """
            UPDATE "Bookings" SET "Source" = 0 WHERE "Source" = 8;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RevertSql);
        }
    }
}
