using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTouristTaxCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CollectedAmount",
                table: "StayTouristTaxes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CollectedAt",
                table: "StayTouristTaxes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CollectionStatus",
                table: "StayTouristTaxes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "StayTouristTaxes",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundedAmount",
                table: "StayTouristTaxes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "StripeAccountId",
                table: "StayTouristTaxes",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "StayTouristTaxes",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TouristTaxOnlinePaymentEnabled",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TouristTaxRequiredBeforeAccessCode",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "UIX_StayTouristTaxes_StripePaymentIntentId",
                table: "StayTouristTaxes",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StayTouristTaxes_RefundedWithinCollected",
                table: "StayTouristTaxes",
                sql: "\"CollectedAmount\" >= 0 AND \"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"CollectedAmount\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_StayTouristTaxes_StripePaymentIntentId",
                table: "StayTouristTaxes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StayTouristTaxes_RefundedWithinCollected",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "CollectedAmount",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "CollectedAt",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "CollectionStatus",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "RefundedAmount",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "StripeAccountId",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "StayTouristTaxes");

            migrationBuilder.DropColumn(
                name: "TouristTaxOnlinePaymentEnabled",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "TouristTaxRequiredBeforeAccessCode",
                table: "Orgs");
        }
    }
}
