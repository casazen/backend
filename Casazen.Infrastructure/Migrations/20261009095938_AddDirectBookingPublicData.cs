using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// DB-03: the stay rules the public quote reads and the public profile of the booking site. <c>Properties.MinNights</c>
    /// (null = no minimum) and <c>Properties.WeekendSurchargePercent</c> (0 = no surcharge) with a CHECK each;
    /// <c>Orgs.Subtitle</c>, <c>Orgs.HostName</c> and <c>Orgs.PublicPhone</c> (all null = nothing published). Every existing
    /// row gets the value that changes nothing, so the price of a stay and the public pages are what they were; no data
    /// is rewritten. Runbook: <c>docs/runbooks/direct-booking.md</c> § 11.
    /// </summary>
    public partial class AddDirectBookingPublicData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinNights",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeekendSurchargePercent",
                table: "Properties",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "HostName",
                table: "Orgs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicPhone",
                table: "Orgs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subtitle",
                table: "Orgs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_MinNights",
                table: "Properties",
                sql: "\"MinNights\" IS NULL OR \"MinNights\" BETWEEN 1 AND 30");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_WeekendSurchargePercent",
                table: "Properties",
                sql: "\"WeekendSurchargePercent\" BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_MinNights",
                table: "Properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_WeekendSurchargePercent",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "MinNights",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "WeekendSurchargePercent",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "HostName",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "PublicPhone",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "Subtitle",
                table: "Orgs");
        }
    }
}
