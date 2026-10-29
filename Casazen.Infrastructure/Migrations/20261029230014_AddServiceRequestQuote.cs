using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRequestQuote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuoteEventsJson",
                table: "ServiceRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "QuoteLinesJson",
                table: "ServiceRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "QuoteNote",
                table: "ServiceRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuoteScheduledEndUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuoteScheduledStartUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuoteSentAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuoteSentByUserId",
                table: "ServiceRequests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteTotalCents",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Quote",
                table: "ServiceRequests",
                sql: "(\"QuoteSentAt\" IS NULL AND \"QuoteSentByUserId\" IS NULL AND \"QuoteTotalCents\" IS NULL AND \"QuoteNote\" IS NULL AND \"QuoteScheduledStartUtc\" IS NULL AND \"QuoteScheduledEndUtc\" IS NULL AND \"QuoteLinesJson\" = '[]'::jsonb) OR (\"QuoteSentAt\" IS NOT NULL AND \"Status\" = 0 AND \"QuoteTotalCents\" BETWEEN 1 AND 10000000 AND jsonb_array_length(\"QuoteLinesJson\") BETWEEN 1 AND 20 AND ((\"QuoteScheduledStartUtc\" IS NULL AND \"QuoteScheduledEndUtc\" IS NULL) OR (\"QuoteScheduledStartUtc\" IS NOT NULL AND \"QuoteScheduledEndUtc\" IS NOT NULL AND \"QuoteScheduledEndUtc\" > \"QuoteScheduledStartUtc\")))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_QuoteOffer",
                table: "ServiceRequests",
                sql: "\"QuoteSentAt\" IS NULL OR \"ProposedStartUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_Quote",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_QuoteOffer",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteEventsJson",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteLinesJson",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteNote",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteScheduledEndUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteScheduledStartUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteSentAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteSentByUserId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuoteTotalCents",
                table: "ServiceRequests");
        }
    }
}
