using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Data of the PM-01 migration: the backfill of <c>Properties.RentalMode</c> (decision D19), with its dry run. Same
    /// convention as <c>AddLtrReferenceDataAdmin.Data.cs</c>: inline values, the migration never reads the application's
    /// code, so it does the same thing whatever the code becomes.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule (D19).</b> The column starts at <c>0</c> = <c>Short</c> for every row (the default, nothing changes
    /// for a short-stay host). A live property becomes <c>1</c> = <c>Long</c> only when the three signals agree:
    /// <list type="number">
    /// <item>it has <b>contracts</b>: at least one lease that is not a draft nor rejected (<c>LeaseStatus</c> 0 and 7);</item>
    /// <item>it has <b>no booking</b> at all, in any status (a booking is the proof that it was let for short stays);</item>
    /// <item>it carries the <b>A7-06 marker</b> of a property kept for long-term leases only: no guests
    /// (<c>MaxGuests = 0</c>) and no nightly rate (<c>NightlyRate = 0</c>), what the long-rent form has always sent.</item>
    /// </list>
    /// The <b>ambiguous</b> cases (contracts <b>and</b> bookings) stay <c>Short</c>, and so do the properties with contracts
    /// and no bookings that nevertheless have guests or a rate: nothing is guessed, the host changes the mode with the
    /// scheduled mode change (PM-02). Soft-deleted properties are left as they are.</para>
    /// <para><b>Dry run.</b> The queries below are read-only and run <b>before</b> the migration, on the database as it is
    /// (they read nothing the migration creates): <see cref="DryRunSummarySql"/> counts the cases per org,
    /// <see cref="DryRunDetailSql"/> lists their ids. They share <see cref="SignalsSql"/> with the backfill, so the list is
    /// exactly what the migration changes. The product owner approves the count of the ambiguous ones with it
    /// (<c>docs/runbooks/property-rental-mode.md</c>).</para>
    /// <para>Idempotent: only the rows still at <c>0</c> are updated, so running the statement again changes nothing.
    /// <c>UpdatedAt</c> is not touched (a classification, not an edit by the host). The counts are logged with
    /// <c>RAISE NOTICE</c>. Public so that a PostgreSQL test runs exactly these statements.</para>
    /// </remarks>
    public partial class AddPropertyRentalMode
    {
        /// <summary>
        /// One row per live property (not soft-deleted) with the signals of D19: <c>Contracts</c> (leases other than draft
        /// and rejected), <c>Bookings</c> (any status) and <c>NoShortStayData</c> (the A7-06 marker). Reads only columns
        /// that exist before the migration.
        /// </summary>
        public const string SignalsSql = """
            SELECT p."Id" AS "PropertyId",
                   p."OrgId" AS "OrgId",
                   (SELECT count(*)
                    FROM "LeaseContracts" AS l
                    WHERE l."PropertyId" = p."Id"
                      AND l."Status" NOT IN (0, 7)) AS "Contracts", -- LeaseStatus.Draft, LeaseStatus.Rejected
                   (SELECT count(*)
                    FROM "Bookings" AS b
                    WHERE b."PropertyId" = p."Id") AS "Bookings",
                   (p."MaxGuests" = 0 AND p."NightlyRate" = 0) AS "NoShortStayData"
            FROM "Properties" AS p
            WHERE p."IsDeleted" = false
            """;

        /// <summary>The backfill: the properties with contracts, no booking and the A7-06 marker become long-term.</summary>
        public const string BackfillSql = $$"""
            DO $$
            DECLARE
                v_long      bigint;
                v_ambiguous bigint;
                v_with_data bigint;
            BEGIN
                WITH signals AS ({{SignalsSql}})
                UPDATE "Properties" AS p
                SET "RentalMode" = 1 -- RentalMode.Long
                FROM signals AS s
                WHERE p."Id" = s."PropertyId"
                  AND p."RentalMode" = 0
                  AND s."Contracts" > 0
                  AND s."Bookings" = 0
                  AND s."NoShortStayData";
                GET DIAGNOSTICS v_long = ROW_COUNT;

                SELECT count(*) INTO v_ambiguous
                FROM ({{SignalsSql}}) AS s
                WHERE s."Contracts" > 0 AND s."Bookings" > 0;

                SELECT count(*) INTO v_with_data
                FROM ({{SignalsSql}}) AS s
                WHERE s."Contracts" > 0 AND s."Bookings" = 0 AND NOT s."NoShortStayData";

                RAISE NOTICE 'AddPropertyRentalMode: % properties set to long-term, % ambiguous (contracts and bookings) left short, % with contracts, no bookings and short-stay data left short',
                    v_long, v_ambiguous, v_with_data;
            END $$;
            """;

        /// <summary>
        /// Dry run 1: the cases per org, and the total (the row without org). <c>ToLong</c> = what the migration sets to
        /// long-term; <c>Ambiguous</c> = contracts and bookings, left short; <c>ContractsWithShortStayData</c> = contracts,
        /// no bookings, but guests or a rate, left short. Only the orgs with at least one property with contracts.
        /// </summary>
        public const string DryRunSummarySql = $$"""
            SELECT s."OrgId",
                   count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" = 0 AND s."NoShortStayData") AS "ToLong",
                   count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" > 0) AS "Ambiguous",
                   count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" = 0 AND NOT s."NoShortStayData") AS "ContractsWithShortStayData"
            FROM ({{SignalsSql}}) AS s
            WHERE s."Contracts" > 0
            GROUP BY ROLLUP (s."OrgId")
            ORDER BY s."OrgId" NULLS LAST
            """;

        /// <summary>
        /// Dry run 2: the properties of those cases, one row each, with their id, org, the case (<c>to_long</c>,
        /// <c>ambiguous</c> or <c>contracts_with_short_stay_data</c>) and the counts that decided it.
        /// </summary>
        public const string DryRunDetailSql = $$"""
            SELECT s."OrgId",
                   s."PropertyId",
                   CASE
                       WHEN s."Bookings" > 0 THEN 'ambiguous'
                       WHEN s."NoShortStayData" THEN 'to_long'
                       ELSE 'contracts_with_short_stay_data'
                   END AS "Case",
                   s."Contracts",
                   s."Bookings"
            FROM ({{SignalsSql}}) AS s
            WHERE s."Contracts" > 0
            ORDER BY s."OrgId", "Case", s."PropertyId"
            """;

        /// <summary>
        /// Before a rollback of the application to a version without the mode: the long-term properties that have guests or
        /// a rate. The old code took a property for long-term only when it had neither, so these would look short-stay.
        /// Run it on the database after the deploy, before the rollback; the migration's own <c>Down</c> only drops the
        /// column.
        /// </summary>
        public const string RollbackCheckSql = """
            SELECT p."OrgId", p."Id" AS "PropertyId", p."MaxGuests", p."NightlyRate"
            FROM "Properties" AS p
            WHERE p."RentalMode" = 1 -- RentalMode.Long
              AND (p."MaxGuests" <> 0 OR p."NightlyRate" <> 0)
            ORDER BY p."OrgId", p."Id"
            """;

        private static void ApplyData(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(BackfillSql);
    }
}
