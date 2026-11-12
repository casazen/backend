using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProviderFees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderAvailableOn",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderBalanceCurrency",
                table: "Payments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderBalanceTransactionId",
                table: "Payments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProviderFeeAmount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderFeeFetchAttempts",
                table: "Payments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderFeeFetchedAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProviderNetAmount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderAvailableOn",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderBalanceCurrency",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderBalanceTransactionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderFeeAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderFeeFetchAttempts",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderFeeFetchedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderNetAmount",
                table: "Payments");
        }
    }
}
