using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRequestSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "ServiceRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledBy",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionNotes",
                table: "ServiceRequests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstimatedAmountCents",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FinalAmountCents",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FinalAmountNeedsConfirmation",
                table: "ServiceRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRemindedAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionsJson",
                table: "ServiceRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "PriceLinesJson",
                table: "ServiceRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ProposalMessage",
                table: "ServiceRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProposedAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedByUserId",
                table: "ServiceRequests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProposedEndUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProposedStartUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuotedAmountCents",
                table: "ServiceRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseDueAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledEndUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledStartUtc",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceListingId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceNameSnapshot",
                table: "ServiceRequests",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkPhotosJson",
                table: "ServiceRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_ServiceListingId",
                table: "ServiceRequests",
                column: "ServiceListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc",
                table: "ServiceRequests",
                columns: new[] { "SupplierOrgId", "ScheduledStartUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Amounts",
                table: "ServiceRequests",
                sql: "(\"EstimatedAmountCents\" IS NULL OR \"EstimatedAmountCents\" BETWEEN 1 AND 10000000) AND (\"QuotedAmountCents\" IS NULL OR \"QuotedAmountCents\" BETWEEN 1 AND 10000000) AND (\"FinalAmountCents\" IS NULL OR \"FinalAmountCents\" BETWEEN 1 AND 10000000)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_ProposedInterval",
                table: "ServiceRequests",
                sql: "(\"ProposedStartUtc\" IS NULL AND \"ProposedEndUtc\" IS NULL AND \"ProposedAt\" IS NULL) OR (\"ProposedStartUtc\" IS NOT NULL AND \"ProposedEndUtc\" IS NOT NULL AND \"ProposedAt\" IS NOT NULL AND \"ProposedEndUtc\" > \"ProposedStartUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_ScheduledInterval",
                table: "ServiceRequests",
                sql: "(\"ScheduledStartUtc\" IS NULL AND \"ScheduledEndUtc\" IS NULL) OR (\"ScheduledStartUtc\" IS NOT NULL AND \"ScheduledEndUtc\" IS NOT NULL AND \"ScheduledEndUtc\" > \"ScheduledStartUtc\")");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_SupplierServiceListings_ServiceListingId",
                table: "ServiceRequests",
                column: "ServiceListingId",
                principalTable: "SupplierServiceListings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_SupplierServiceListings_ServiceListingId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_ServiceListingId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_Amounts",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_ProposedInterval",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_ScheduledInterval",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "CompletionNotes",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "EstimatedAmountCents",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "FinalAmountCents",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "FinalAmountNeedsConfirmation",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LastRemindedAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "OptionsJson",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "PriceLinesJson",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ProposalMessage",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ProposedAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ProposedByUserId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ProposedEndUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ProposedStartUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "QuotedAmountCents",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ResponseDueAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ScheduledEndUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ScheduledStartUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ServiceListingId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ServiceNameSnapshot",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "WorkPhotosJson",
                table: "ServiceRequests");
        }
    }
}
