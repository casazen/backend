using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// LT-04 (A7-04): the RLI deadline was <c>StartDate + 30</c>; the rule is <c>min(stipula, start) + 30</c> days
    /// (<c>RliRegistrationDeadline</c>, fiscale.md L1).
    /// </summary>
    /// <remarks>
    /// <para>Schema: <c>StipulaDate</c> (nullable) and <c>RegistrationDeadline</c> nullable (unknown without a stipula).</para>
    /// <para>Data, in this order:</para>
    /// <list type="number">
    /// <item><see cref="BackfillStipulaDateSql"/>: stipula = Europe/Rome date of the first <c>AllPartiesSigned</c> (3)
    /// event. Leases without that event keep a null stipula (never guessed).</item>
    /// <item><see cref="BindOldRemindersToTheirDeadlineSql"/>: the reminders already sent by the old job
    /// (<c>DeadlineReminderSent</c> (12) with payload <c>t-15</c>, <c>t-7</c>, <c>t-1</c>, <c>overdue</c>) get the
    /// deadline they were computed on (<c>t-15:2026-10-01</c>), the format of the new job: a reminder is not repeated
    /// when the corrected deadline is the same date, and is sent for the corrected deadline when it differs.</item>
    /// <item><see cref="RecomputeRegistrationDeadlineSql"/>: deadline = <c>min(stipula, start) + 30</c> days where the
    /// stipula is known, null otherwise (the API resolves it for a lease not signed yet whose start date has come).</item>
    /// </list>
    /// <para>Down puts back <c>StartDate + 30</c> where the deadline is null and the old reminder payloads.
    /// Runbook: <c>docs/runbooks/rli.md</c>.</para>
    /// </remarks>
    public partial class AddLeaseStipulaDate : Migration
    {
        /// <summary>Stipula from the first AllPartiesSigned event, as midnight UTC of its Europe/Rome date.</summary>
        public const string BackfillStipulaDateSql = """
            UPDATE "LeaseContracts" AS l
            SET "StipulaDate" = (((s."FirstSignedAt" AT TIME ZONE 'Europe/Rome')::date)::timestamp AT TIME ZONE 'UTC')
            FROM (
                SELECT "LeaseContractId", min("OccurredAt") AS "FirstSignedAt"
                FROM "LeaseEvents"
                WHERE "EventType" = 3
                GROUP BY "LeaseContractId"
            ) AS s
            WHERE s."LeaseContractId" = l."Id" AND l."StipulaDate" IS NULL;
            """;

        /// <summary>Old reminder payloads bound to the deadline they were sent for (before it is recomputed).</summary>
        public const string BindOldRemindersToTheirDeadlineSql = """
            UPDATE "LeaseEvents" AS e
            SET "Payload" = e."Payload" || ':' || to_char((l."RegistrationDeadline" AT TIME ZONE 'Europe/Rome')::date, 'YYYY-MM-DD')
            FROM "LeaseContracts" AS l
            WHERE e."LeaseContractId" = l."Id"
              AND e."EventType" = 12
              AND e."Payload" IN ('t-15', 't-7', 't-1', 'overdue')
              AND l."RegistrationDeadline" IS NOT NULL;
            """;

        /// <summary>min(stipula, start) + 30 days on the Europe/Rome calendar, midnight UTC; null without a stipula.</summary>
        public const string RecomputeRegistrationDeadlineSql = """
            UPDATE "LeaseContracts"
            SET "RegistrationDeadline" = CASE
                WHEN "StipulaDate" IS NULL THEN NULL
                ELSE ((LEAST(("StipulaDate" AT TIME ZONE 'Europe/Rome')::date,
                             ("StartDate" AT TIME ZONE 'Europe/Rome')::date) + 30)::timestamp AT TIME ZONE 'UTC')
            END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "RegistrationDeadline",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "StipulaDate",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(BackfillStipulaDateSql);
            migrationBuilder.Sql(BindOldRemindersToTheirDeadlineSql);
            migrationBuilder.Sql(RecomputeRegistrationDeadlineSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "LeaseEvents"
                SET "Payload" = split_part("Payload", ':', 1)
                WHERE "EventType" = 12 AND "Payload" ~ '^(t-15|t-7|t-1|overdue):';

                UPDATE "LeaseContracts"
                SET "RegistrationDeadline" = "StartDate" + interval '30 days'
                WHERE "RegistrationDeadline" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "StipulaDate",
                table: "LeaseContracts");

            migrationBuilder.AlterColumn<DateTime>(
                name: "RegistrationDeadline",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
