using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceDataOctorateBillingSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OctorateBillingHash",
                table: "BookingInvoiceData",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OctorateBillingSentAt",
                table: "BookingInvoiceData",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OctorateBillingHash",
                table: "BookingInvoiceData");

            migrationBuilder.DropColumn(
                name: "OctorateBillingSentAt",
                table: "BookingInvoiceData");
        }
    }
}
