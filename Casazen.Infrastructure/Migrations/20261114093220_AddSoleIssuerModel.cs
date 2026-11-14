using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoleIssuerModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoIssueGuestDocuments",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CleaningFeeTreatment",
                table: "OrgFiscalIssuerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ConservationAgreementConfirmed",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmailGuestDocuments",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GuestDocumentSeries",
                table: "OrgFiscalIssuerProfiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerInvoiceSeries",
                table: "OrgFiscalIssuerProfiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformCollectedTouristTaxChannels",
                table: "OrgFiscalIssuerProfiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QrIban",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StampDutyReference",
                table: "OrgFiscalIssuerProfiles",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StayVatTreatment",
                table: "OrgFiscalIssuerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SwissVatNumber",
                table: "OrgFiscalIssuerProfiles",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VatRegistered",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "VirtualStampConfirmed",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CreditedInFull",
                table: "FiscalDocuments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IssuerSnapshot",
                table: "FiscalDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerStatementVersionId",
                table: "FiscalDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentChannel",
                table: "FiscalDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PeriodMonth",
                table: "FiscalDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeriodYear",
                table: "FiscalDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "FiscalDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Purpose",
                table: "FiscalDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientOwnerId",
                table: "FiscalDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleVersion",
                table: "FiscalDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodFrom",
                table: "FiscalDocuments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodTo",
                table: "FiscalDocuments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LineType",
                table: "FiscalDocumentLines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "VatAmount",
                table: "FiscalDocumentLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // E11/F01/US01/T01, decision of the PO of 2026-11-14: CasaZen is the only issuer. The data of the rows already stored, before the old column goes.
            // Every profile becomes CasaZen (the retired modes Octorate = 1 and accountant = 3 become 2); the treatments get their prudent defaults (the
            // rate of the stay, the cleaning with the stay); the VAT registration follows the regime; the old default series becomes the series of the guest
            // documents when it fits the 10 characters of a series (a longer one was refused at the issue anyway).
            migrationBuilder.Sql(
                """
                UPDATE "OrgFiscalIssuerProfiles" SET "IssuerMode" = 2 WHERE "IssuerMode" <> 2;
                UPDATE "OrgFiscalIssuerProfiles" SET "StayVatTreatment" = 1, "CleaningFeeTreatment" = 1;
                UPDATE "OrgFiscalIssuerProfiles" SET "VatRegistered" = TRUE WHERE "DocumentRegime" IN (2, 3);
                UPDATE "OrgFiscalIssuerProfiles" SET "GuestDocumentSeries" = "DefaultSeries"
                    WHERE "DefaultSeries" IS NOT NULL AND length("DefaultSeries") <= 10;
                """);

            // Every stored document is the document of the stay of a guest, about the property of its booking; every stored line is lodging, except the
            // tourist tax line, written with its fixed description (also at the end of the line of a credit note).
            migrationBuilder.Sql(
                """
                UPDATE "FiscalDocuments" SET "Purpose" = 1;
                UPDATE "FiscalDocuments" AS d SET "PropertyId" = b."PropertyId"
                    FROM "Bookings" AS b WHERE d."BookingId" = b."Id" AND d."PropertyId" IS NULL;
                UPDATE "FiscalDocumentLines" SET "LineType" = 1;
                UPDATE "FiscalDocumentLines" SET "LineType" = 4 WHERE "Description" LIKE '%Imposta di soggiorno riscossa per conto del comune';
                """);

            migrationBuilder.DropColumn(
                name: "DefaultSeries",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_OwnerStatementVersionId",
                table: "FiscalDocuments",
                column: "OwnerStatementVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_PropertyId",
                table: "FiscalDocuments",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_RecipientOwnerId",
                table: "FiscalDocuments",
                column: "RecipientOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "RecipientOwnerId", "PeriodYear", "PeriodMonth" },
                unique: true,
                filter: "\"Purpose\" = 2 AND \"Status\" <> 3 AND \"Kind\" <> 3 AND NOT \"CreditedInFull\" AND \"RecipientOwnerId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalDocuments_OwnerStatementVersions_OwnerStatementVersio~",
                table: "FiscalDocuments",
                column: "OwnerStatementVersionId",
                principalTable: "OwnerStatementVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalDocuments_Properties_PropertyId",
                table: "FiscalDocuments",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalDocuments_PropertyOwners_RecipientOwnerId",
                table: "FiscalDocuments",
                column: "RecipientOwnerId",
                principalTable: "PropertyOwners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The series of the guest documents goes back to the old column before it is dropped; the issuer modes stay CasaZen (2).
            migrationBuilder.AddColumn<string>(
                name: "DefaultSeries",
                table: "OrgFiscalIssuerProfiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql("""UPDATE "OrgFiscalIssuerProfiles" SET "DefaultSeries" = "GuestDocumentSeries" WHERE "GuestDocumentSeries" IS NOT NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_FiscalDocuments_OwnerStatementVersions_OwnerStatementVersio~",
                table: "FiscalDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_FiscalDocuments_Properties_PropertyId",
                table: "FiscalDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_FiscalDocuments_PropertyOwners_RecipientOwnerId",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "IX_FiscalDocuments_OwnerStatementVersionId",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "IX_FiscalDocuments_PropertyId",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "IX_FiscalDocuments_RecipientOwnerId",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "AutoIssueGuestDocuments",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "CleaningFeeTreatment",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "ConservationAgreementConfirmed",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "EmailGuestDocuments",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "GuestDocumentSeries",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "OwnerInvoiceSeries",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "PlatformCollectedTouristTaxChannels",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "QrIban",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "StampDutyReference",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "StayVatTreatment",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "SwissVatNumber",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "VatRegistered",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "VirtualStampConfirmed",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "CreditedInFull",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "IssuerSnapshot",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "OwnerStatementVersionId",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "PaymentChannel",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "PeriodMonth",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "PeriodYear",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "RecipientOwnerId",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ServicePeriodFrom",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ServicePeriodTo",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "LineType",
                table: "FiscalDocumentLines");

            migrationBuilder.DropColumn(
                name: "VatAmount",
                table: "FiscalDocumentLines");
        }
    }
}
