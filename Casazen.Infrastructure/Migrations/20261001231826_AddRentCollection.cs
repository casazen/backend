using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRentCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "RentLedgerEntries",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "RentLedgerEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFailedAt",
                table: "RentLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarkedPaidByUserId",
                table: "RentLedgerEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfflinePaymentNote",
                table: "RentLedgerEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PaidOn",
                table: "RentLedgerEntries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidVia",
                table: "RentLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentIntentCount",
                table: "RentLedgerEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentRequestedAt",
                table: "RentLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentTokenHash",
                table: "RentLedgerEntries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "RentLedgerEntries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "LastFailedAt",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "MarkedPaidByUserId",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "OfflinePaymentNote",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaidOn",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaidVia",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaymentIntentCount",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaymentRequestedAt",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaymentTokenHash",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "RentLedgerEntries");
        }
    }
}
