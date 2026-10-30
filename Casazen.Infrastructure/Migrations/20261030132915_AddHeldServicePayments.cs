using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHeldServicePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAt",
                table: "ShowcaseBookingHolds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentAmountCents",
                table: "ShowcaseBookingHolds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentAuthorizedAt",
                table: "ShowcaseBookingHolds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "ShowcaseBookingHolds",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorizationExpiresAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorizedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CapturedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CardDisputeAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardDisputeId",
                table: "ServiceRequestPayments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardDisputeStatus",
                table: "ServiceRequestPayments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FundsModel",
                table: "ServiceRequestPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "HoldWarnedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleaseAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleaseConfirmedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleasedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeChargeId",
                table: "ServiceRequestPayments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeTransferId",
                table: "ServiceRequestPayments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TransferAlertedAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferAttempts",
                table: "ServiceRequestPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TransferFailureCode",
                table: "ServiceRequestPayments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferGroup",
                table: "ServiceRequestPayments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TransferNextAttemptAt",
                table: "ServiceRequestPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_ShowcaseBookingHolds_StripePaymentIntentId",
                table: "ShowcaseBookingHolds",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShowcaseBookingHolds_Payment",
                table: "ShowcaseBookingHolds",
                sql: "(\"StripePaymentIntentId\" IS NULL AND \"PaymentAmountCents\" IS NULL AND \"PaymentAuthorizedAt\" IS NULL) OR (\"StripePaymentIntentId\" IS NOT NULL AND \"PaymentAmountCents\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestPayments_Held_Status_ReleaseAt",
                table: "ServiceRequestPayments",
                columns: new[] { "Status", "ReleaseAt" },
                filter: "\"FundsModel\" = 1");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequestPayments_StripeTransferId",
                table: "ServiceRequestPayments",
                column: "StripeTransferId",
                unique: true,
                filter: "\"StripeTransferId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequestPayments_Funds",
                table: "ServiceRequestPayments",
                sql: "\"FundsModel\" IN (0, 1) AND (\"Status\" < 8 OR \"FundsModel\" = 1) AND (\"FundsModel\" = 0 OR \"ConnectedAccountId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequestPayments_Held",
                table: "ServiceRequestPayments",
                sql: "(\"Status\" NOT IN (9, 10, 11, 12) OR \"CapturedAt\" IS NOT NULL) AND (\"Status\" <> 10 OR (\"StripeTransferId\" IS NOT NULL AND \"ReleasedAt\" IS NOT NULL)) AND \"TransferAttempts\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_ShowcaseBookingHolds_StripePaymentIntentId",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShowcaseBookingHolds_Payment",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequestPayments_Held_Status_ReleaseAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropIndex(
                name: "UIX_ServiceRequestPayments_StripeTransferId",
                table: "ServiceRequestPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequestPayments_Funds",
                table: "ServiceRequestPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequestPayments_Held",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAt",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropColumn(
                name: "PaymentAmountCents",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropColumn(
                name: "PaymentAuthorizedAt",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "ShowcaseBookingHolds");

            migrationBuilder.DropColumn(
                name: "AuthorizationExpiresAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "AuthorizedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "CapturedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "CardDisputeAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "CardDisputeId",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "CardDisputeStatus",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "FundsModel",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "HoldWarnedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "ReleaseAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "ReleaseConfirmedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "StripeChargeId",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "StripeTransferId",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "TransferAlertedAt",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "TransferAttempts",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "TransferFailureCode",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "TransferGroup",
                table: "ServiceRequestPayments");

            migrationBuilder.DropColumn(
                name: "TransferNextAttemptAt",
                table: "ServiceRequestPayments");
        }
    }
}
