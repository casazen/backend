using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentOptionToBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FreeRefundDeadline",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentOption",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "Bookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentMethodId",
                table: "Bookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSetupIntentId",
                table: "Bookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FreeRefundDeadline",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaymentOption",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "StripePaymentMethodId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "StripeSetupIntentId",
                table: "Bookings");
        }
    }
}
