using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-11 (A5-01, A9-05, A5-03, A5-37): honest Alloggiati Web status. Until CO-11 every report was set to
    /// <c>Submitted</c> (1) without transmitting anything, so no existing row has a real receipt.
    /// </summary>
    /// <remarks>
    /// <para>Schema: <c>Bookings.ArrivedAt</c> (real arrival, the 24/6-hour term runs from it), the Hangfire job
    /// scheduled for the arrival day (<c>ScheduledJobId</c>, <c>ScheduledFor</c>), <c>ReportedAt</c> nullable (null while
    /// not sent), a unique index on (<c>BookingId</c>, <c>GuestId</c>) as idempotency key, and the check constraint
    /// <c>CK_AlloggiatiWebReports_SentRequiresReceipt</c>: status <c>Inviato</c> (2) only with a receipt reference.</para>
    /// <para>Data, before the index and the constraint (see <see cref="DeduplicateReportsSql"/>,
    /// <see cref="HonestStatusSql"/>, <see cref="SessionsSql"/>): duplicates of the same booking and guest are merged
    /// into the latest row; <c>Submitted</c> (1), <c>Confirmed</c> (2) and <c>Failed</c> (3) without a receipt become
    /// <c>DaInviareManualmente</c> (4) with no error text, no sent date and no "manually completed" flag (the old flag
    /// meant "the host pressed the simulated send"); a <c>Submitted</c> row with a receipt becomes <c>Inviato</c> (2).
    /// Guest check-in sessions set to <c>AlloggiatiInviato</c> (3) on queueing go back to <c>Completo</c> (2) unless
    /// their booking has a report with a receipt.</para>
    /// <para>Down restores the old schema; data best effort (4 → 0 pending, 5 → 3 failed, 6 → 1 submitted, missing
    /// <c>ReportedAt</c> ← <c>UpdatedAt</c>). Merged duplicates are not restored. Runbook: <c>docs/runbooks/alloggiati.md</c>.</para>
    /// </remarks>
    public partial class AlloggiatiHonestStatus : Migration
    {
        /// <summary>Keeps one row per (BookingId, GuestId): the most recently updated one.</summary>
        public const string DeduplicateReportsSql = """
            DELETE FROM "AlloggiatiWebReports" AS r
            USING "AlloggiatiWebReports" AS newer
            WHERE newer."BookingId" = r."BookingId"
              AND newer."GuestId" = r."GuestId"
              AND (newer."UpdatedAt", newer."Id") > (r."UpdatedAt", r."Id");
            """;

        /// <summary>Old statuses (0 Pending, 1 Submitted, 2 Confirmed, 3 Failed) to the honest ones.</summary>
        public const string HonestStatusSql = """
            UPDATE "AlloggiatiWebReports"
            SET "Status" = 2, "UpdatedAt" = now()
            WHERE "Status" = 1 AND btrim(coalesce("ConfirmationNumber", '')) <> '';

            UPDATE "AlloggiatiWebReports"
            SET "Status" = 4, "ErrorMessage" = NULL, "ManuallyCompleted" = false, "UpdatedAt" = now()
            WHERE "Status" IN (1, 2, 3) AND btrim(coalesce("ConfirmationNumber", '')) = '';

            UPDATE "AlloggiatiWebReports"
            SET "ReportedAt" = NULL
            WHERE "Status" NOT IN (2, 6);
            """;

        /// <summary>Check-in sessions marked "Alloggiati sent" on queueing go back to "complete".</summary>
        public const string SessionsSql = """
            UPDATE "GuestCheckInSessions" AS s
            SET "Status" = 2, "UpdatedAt" = now()
            WHERE s."Status" = 3
              AND NOT EXISTS (
                  SELECT 1 FROM "AlloggiatiWebReports" AS r
                  WHERE r."BookingId" = s."BookingId" AND r."Status" = 2);
            """;

        /// <summary>Best-effort data rollback so that the old code can read every row.</summary>
        public const string DownDataSql = """
            UPDATE "AlloggiatiWebReports"
            SET "Status" = CASE "Status" WHEN 4 THEN 0 WHEN 5 THEN 3 WHEN 6 THEN 1 ELSE "Status" END
            WHERE "Status" IN (4, 5, 6);

            UPDATE "AlloggiatiWebReports"
            SET "ReportedAt" = "UpdatedAt"
            WHERE "ReportedAt" IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DeduplicateReportsSql);

            migrationBuilder.DropIndex(
                name: "IX_AlloggiatiWebReports_BookingId",
                table: "AlloggiatiWebReports");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ReportedAt",
                table: "AlloggiatiWebReports",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledFor",
                table: "AlloggiatiWebReports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduledJobId",
                table: "AlloggiatiWebReports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(HonestStatusSql);
            migrationBuilder.Sql(SessionsSql);

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_BookingId_GuestId",
                table: "AlloggiatiWebReports",
                columns: new[] { "BookingId", "GuestId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AlloggiatiWebReports_SentRequiresReceipt",
                table: "AlloggiatiWebReports",
                sql: "\"Status\" <> 2 OR btrim(coalesce(\"ConfirmationNumber\", '')) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlloggiatiWebReports_BookingId_GuestId",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AlloggiatiWebReports_SentRequiresReceipt",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropColumn(
                name: "ArrivedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ScheduledFor",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropColumn(
                name: "ScheduledJobId",
                table: "AlloggiatiWebReports");

            migrationBuilder.Sql(DownDataSql);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ReportedAt",
                table: "AlloggiatiWebReports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_BookingId",
                table: "AlloggiatiWebReports",
                column: "BookingId");
        }
    }
}
