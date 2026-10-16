using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidedLeaseDraft : Migration
    {
        /// <summary>
        /// LR-12: the drafts that exist now were all created complete in one call (<c>POST api/leases</c> asks for every value of the
        /// data steps), so each has been through steps 1 to 5 and opens in the guided draft with those steps done. Only the rows still
        /// at the column's starting value are touched, so a run over drafts the new code already advanced changes nothing; the other
        /// new columns start empty and nothing else is invented.
        /// </summary>
        public const string BackfillSql = """UPDATE "LeaseContracts" SET "WizardMaxStep" = 5 WHERE "Status" = 0 AND "WizardMaxStep" = 0;""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentsVerifiedOn",
                table: "Parties",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Residence",
                table: "Parties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftData",
                table: "LeaseContracts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftVersion",
                table: "LeaseContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "LeaseContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewConfirmedAt",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewFingerprint",
                table: "LeaseContracts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentForSignatureAt",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WizardMaxStep",
                table: "LeaseContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentsVerifiedOn",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Residence",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "DraftData",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "DraftVersion",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ReviewConfirmedAt",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ReviewFingerprint",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "SentForSignatureAt",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "WizardMaxStep",
                table: "LeaseContracts");
        }
    }
}
