using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerIssuerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_OrgFiscalIssuerProfiles_OrgId",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropIndex(
                name: "UIX_FiscalNumberSequences_OrgId_Series",
                table: "FiscalNumberSequences");

            migrationBuilder.DropIndex(
                name: "UIX_FiscalDocuments_OrgId_Series_SequenceNumber",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "UIX_BookingDocumentIssuers_BookingId",
                table: "BookingDocumentIssuers");

            migrationBuilder.AlterColumn<string>(
                name: "PostalCode",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LegalName",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IssuerPec",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "City",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AddressStreet",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AddressCivic",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalCode",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StayIssuedByOwner",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VatNumber",
                table: "OrgFiscalIssuerProfiles",
                type: "text",
                nullable: true);

            // E17/F01/US01/T01: the counters are per issuer. Added empty, moved to the profile of their organization below, then required.
            migrationBuilder.AddColumn<Guid>(
                name: "IssuerProfileId",
                table: "FiscalNumberSequences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForAgencyServices",
                table: "FiscalDocuments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "IssuerProfileId",
                table: "FiscalDocuments",
                type: "uuid",
                nullable: true);

            // Every existing document and counter is the organization's: they move to the profile of their organization (the only profile there was). A counter
            // of an organization without a profile cannot have issued anything: it is deleted, and the next issue seeds it again above the highest number used.
            migrationBuilder.Sql(
                """
                UPDATE "FiscalDocuments" AS d SET "IssuerProfileId" = p."Id"
                    FROM "OrgFiscalIssuerProfiles" AS p
                    WHERE p."OrgId" = d."OrgId" AND p."PropertyOwnerId" IS NULL AND d."IssuerProfileId" IS NULL;
                UPDATE "FiscalNumberSequences" AS s SET "IssuerProfileId" = p."Id"
                    FROM "OrgFiscalIssuerProfiles" AS p
                    WHERE p."OrgId" = s."OrgId" AND p."PropertyOwnerId" IS NULL AND s."IssuerProfileId" IS NULL;
                DELETE FROM "FiscalNumberSequences" WHERE "IssuerProfileId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "IssuerProfileId",
                table: "FiscalNumberSequences",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OrgFiscalIssuerProfiles_OrgId",
                table: "OrgFiscalIssuerProfiles",
                column: "OrgId",
                unique: true,
                filter: "\"PropertyOwnerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_OrgFiscalIssuerProfiles_PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles",
                column: "PropertyOwnerId",
                unique: true,
                filter: "\"PropertyOwnerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalNumberSequences_OrgId_IssuerProfileId_Series",
                table: "FiscalNumberSequences",
                columns: new[] { "OrgId", "IssuerProfileId", "Series" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_IssuerProfileId",
                table: "FiscalDocuments",
                column: "IssuerProfileId");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalDocuments_OrgId_IssuerProfileId_Series_SequenceNumber",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "IssuerProfileId", "Series", "SequenceNumber" },
                unique: true,
                filter: "\"SequenceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookingDocumentIssuers_BookingId",
                table: "BookingDocumentIssuers",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "UIX_BookingDocumentIssuers_LockedByDocumentId",
                table: "BookingDocumentIssuers",
                column: "LockedByDocumentId",
                unique: true,
                filter: "\"LockedByDocumentId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalDocuments_OrgFiscalIssuerProfiles_IssuerProfileId",
                table: "FiscalDocuments",
                column: "IssuerProfileId",
                principalTable: "OrgFiscalIssuerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrgFiscalIssuerProfiles_PropertyOwners_PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles",
                column: "PropertyOwnerId",
                principalTable: "PropertyOwners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiscalDocuments_OrgFiscalIssuerProfiles_IssuerProfileId",
                table: "FiscalDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_OrgFiscalIssuerProfiles_PropertyOwners_PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles");

            // The version before knows one profile, one counter per series and one issuer row per booking: the owners' counters and profiles go, and a booking
            // keeps its first issuer row. A number an owner gave in a series of its organization makes the old unique index of the documents fail: roll back
            // before any owner document is issued (docs/runbooks/fiscal-documents.md).
            migrationBuilder.Sql(
                """
                DELETE FROM "FiscalNumberSequences" AS s USING "OrgFiscalIssuerProfiles" AS p
                    WHERE s."IssuerProfileId" = p."Id" AND p."PropertyOwnerId" IS NOT NULL;
                DELETE FROM "OrgFiscalIssuerProfiles" WHERE "PropertyOwnerId" IS NOT NULL;
                DELETE FROM "BookingDocumentIssuers" AS later USING "BookingDocumentIssuers" AS first
                    WHERE later."BookingId" = first."BookingId" AND (later."LockedAt", later."Id") > (first."LockedAt", first."Id");
                """);

            migrationBuilder.DropIndex(
                name: "UIX_OrgFiscalIssuerProfiles_OrgId",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropIndex(
                name: "UIX_OrgFiscalIssuerProfiles_PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropIndex(
                name: "UIX_FiscalNumberSequences_OrgId_IssuerProfileId_Series",
                table: "FiscalNumberSequences");

            migrationBuilder.DropIndex(
                name: "IX_FiscalDocuments_IssuerProfileId",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "UIX_FiscalDocuments_OrgId_IssuerProfileId_Series_SequenceNumber",
                table: "FiscalDocuments");

            migrationBuilder.DropIndex(
                name: "IX_BookingDocumentIssuers_BookingId",
                table: "BookingDocumentIssuers");

            migrationBuilder.DropIndex(
                name: "UIX_BookingDocumentIssuers_LockedByDocumentId",
                table: "BookingDocumentIssuers");

            migrationBuilder.DropColumn(
                name: "FiscalCode",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "PropertyOwnerId",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "StayIssuedByOwner",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "VatNumber",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "IssuerProfileId",
                table: "FiscalNumberSequences");

            migrationBuilder.DropColumn(
                name: "ForAgencyServices",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "IssuerProfileId",
                table: "FiscalDocuments");

            // The personal columns of the profiles stay text: once the new version started they hold encrypted payloads, longer than the old lengths (a
            // shorter type would refuse them). The owners' profiles, their counters and the extra issuer rows of a booking must be gone first (see the runbook).
            migrationBuilder.CreateIndex(
                name: "UIX_OrgFiscalIssuerProfiles_OrgId",
                table: "OrgFiscalIssuerProfiles",
                column: "OrgId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalNumberSequences_OrgId_Series",
                table: "FiscalNumberSequences",
                columns: new[] { "OrgId", "Series" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalDocuments_OrgId_Series_SequenceNumber",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "Series", "SequenceNumber" },
                unique: true,
                filter: "\"SequenceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_BookingDocumentIssuers_BookingId",
                table: "BookingDocumentIssuers",
                column: "BookingId",
                unique: true);
        }
    }
}
