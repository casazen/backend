using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// LR-04a. Two columns on the rent plan. <c>OnlineRequestsEnabled</c> says whether the daily job emails the tenants the payment
    /// links: the column's default is <b>false</b> (a plan nobody switched on writes to no one, also one inserted by the previous
    /// release while this migration is applied), and the plans that exist <b>now</b> are set to <b>true</b> by the statement below, so
    /// the job keeps doing for them exactly what it did (decision of the product owner, 17 October 2026: no automatic e-mail from the
    /// new plans). Nothing else of an existing plan or installment is touched. <c>TrackFrom</c> is the day the rents are tracked from:
    /// null for every plan that exists (the whole lease, as before).
    /// </summary>
    public partial class AddRentPlanTrackFromAndOnlineRequests : Migration
    {
        /// <summary>Every plan that exists when the migration runs keeps asking the tenants online, as it always did.</summary>
        public const string BackfillSql = """
            UPDATE "RentSchedules" SET "OnlineRequestsEnabled" = TRUE;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnlineRequestsEnabled",
                table: "RentSchedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TrackFrom",
                table: "RentSchedules",
                type: "date",
                nullable: true);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnlineRequestsEnabled",
                table: "RentSchedules");

            migrationBuilder.DropColumn(
                name: "TrackFrom",
                table: "RentSchedules");
        }
    }
}
