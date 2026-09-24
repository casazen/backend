using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SU-07 (decision D2, A4-13): <c>ServiceRequests.RentalContext</c> (0 = short-rent, 1 = long-rent). Every existing
    /// request gets 0: before SU-07 requests were created only in the short-rent context (web marketplace, app booking
    /// screen, check-out wizard).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Then, prudently, the short-rent requests without <c>BookingId</c> (the web created them without a stay) are tied to
    /// their stay <b>only when it is unique</b>: the request's creation day (Europe/Rome) falls within exactly one
    /// non-cancelled booking of the same property and org, check-in and check-out days included. A request created
    /// between stays, or on a turnover day shared by two stays, is left on its property (<c>BookingId</c> null): it is
    /// listed with the property (<c>GET /api/service-requests?propertyId=</c>) and in no stay. <c>UpdatedAt</c> is not
    /// touched. The counts are logged with <c>RAISE NOTICE</c>. Idempotent. Down drops the column and keeps the links
    /// (they are valid for the old code too). Runbook: <c>docs/runbooks/suppliers.md</c> section 7.
    /// </para>
    /// </remarks>
    public partial class AddServiceRequestRentalContext : Migration
    {
        /// <summary>The data part (one <c>DO</c> block). Public so that a PostgreSQL test can run it again.</summary>
        public const string LinkToStaysSql = """
            DO $$
            DECLARE
                v_linked bigint;
                v_left   bigint;
            BEGIN
                WITH matches AS (
                    SELECT sr."Id" AS request_id,
                           (array_agg(b."Id"))[1] AS booking_id,
                           count(*) AS stays
                    FROM "ServiceRequests" AS sr
                    JOIN "Bookings" AS b
                      ON b."PropertyId" = sr."PropertyId"
                     AND b."OrgId" = sr."OrgId"
                     AND b."Status" <> 4 -- BookingStatus.Cancelled
                     AND (sr."CreatedAt" AT TIME ZONE 'Europe/Rome')::date
                         BETWEEN (b."CheckInDate" AT TIME ZONE 'Europe/Rome')::date
                             AND (b."CheckOutDate" AT TIME ZONE 'Europe/Rome')::date
                    WHERE sr."BookingId" IS NULL
                      AND sr."RentalContext" = 0
                    GROUP BY sr."Id"
                )
                UPDATE "ServiceRequests" AS sr
                SET "BookingId" = m.booking_id
                FROM matches AS m
                WHERE sr."Id" = m.request_id
                  AND m.stays = 1;
                GET DIAGNOSTICS v_linked = ROW_COUNT;

                SELECT count(*) INTO v_left
                FROM "ServiceRequests"
                WHERE "BookingId" IS NULL AND "RentalContext" = 0;

                RAISE NOTICE 'AddServiceRequestRentalContext: % short-rent requests tied to their stay, % left on their property (no single stay)',
                    v_linked, v_left;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RentalContext",
                table: "ServiceRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(LinkToStaysSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RentalContext",
                table: "ServiceRequests");
        }
    }
}
