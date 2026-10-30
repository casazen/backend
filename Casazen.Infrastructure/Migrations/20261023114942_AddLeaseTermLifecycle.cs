using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseTermLifecycle : Migration
    {
        /// <summary>
        /// LR-14. First statement: until now the end of a lease was its <c>EndDate</c>, so the end of the period the lease is in is that
        /// same date for every row (only the rows still at the column's starting value, which the database stores as <c>-infinity</c>:
        /// <c>DateTime.MinValue</c> of the column default, so anything before the year 2 is the starting value, and a run over leases the new
        /// code renewed changes nothing). Second statement: <b>no retroactive renewal</b> (decision of the product owner, 17 October 2026): a signed lease of a type
        /// that renews by itself (libero 0, concordato 1; signed 3, registration pending 4, sent to the provider 5, registered 6) whose last
        /// day had already passed on the day of the migration (Europe/Rome) stays concluded, as the system always showed it. It is recorded as
        /// ended on its last day (<c>TerminatedOn</c> = <c>EndDate</c>), which is what keeps the renewal job away from it: no period is
        /// invented, no lease that ended before the renewal existed starts again. A lease that was renewed in the meantime, one with a notice,
        /// or one that ended after the migration is not touched. A landlord who says one of them did go on can have <c>TerminatedOn</c> cleared
        /// by support: the next run of the job then brings the lease forward period by period.
        /// </summary>
        public const string BackfillSql = """
            UPDATE "LeaseContracts" SET "CurrentTermEndDate" = "EndDate" WHERE "CurrentTermEndDate" < TIMESTAMPTZ '0002-01-01 00:00:00+00';
            UPDATE "LeaseContracts" SET "TerminatedOn" = "EndDate"
            WHERE "TerminatedOn" IS NULL
              AND "NoticeOn" IS NULL
              AND "CurrentTermEndDate" = "EndDate"
              AND "Status" IN (3, 4, 5, 6)
              AND "ContractType" IN (0, 1)
              AND "EndDate" < (date_trunc('day', now() AT TIME ZONE 'Europe/Rome') AT TIME ZONE 'UTC');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentTermEndDate",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "NoticeFrom",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoticeOn",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TerminatedOn",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseContracts_OrgId_CurrentTermEndDate",
                table: "LeaseContracts",
                columns: new[] { "OrgId", "CurrentTermEndDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeaseContracts_OrgId_CurrentTermEndDate",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "CurrentTermEndDate",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "NoticeFrom",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "NoticeOn",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "TerminatedOn",
                table: "LeaseContracts");
        }
    }
}
