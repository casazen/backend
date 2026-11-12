using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerPayoutsAndTaxEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FilingState",
                table: "F24Records",
                type: "integer",
                nullable: false,
                defaultValue: 1); // the existing F24 records are generated data, never filed ones (TaxFilingState.Generated)

            migrationBuilder.AddColumn<DateTime>(
                name: "FilingUpdatedAt",
                table: "F24Records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilingUpdatedByUserId",
                table: "F24Records",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoffReference",
                table: "F24Records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptReference",
                table: "F24Records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "F24Records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleVersion",
                table: "F24Records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OwnerPayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerStatementVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WithholdingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AgencyCollected = table.Column<bool>(type: "boolean", nullable: true),
                    QualifyingShortLet = table.Column<bool>(type: "boolean", nullable: true),
                    ComponentsJson = table.Column<string>(type: "text", nullable: true),
                    StayIdsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerPayouts", x => x.Id);
                    table.CheckConstraint("CK_OwnerPayouts_Amounts", "\"GrossAmount\" > 0 AND \"WithholdingAmount\" >= 0 AND \"NetAmount\" = \"GrossAmount\" - \"WithholdingAmount\"");
                    table.ForeignKey(
                        name: "FK_OwnerPayouts_OwnerStatementVersions_OwnerStatementVersionId",
                        column: x => x.OwnerStatementVersionId,
                        principalTable: "OwnerStatementVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerPayouts_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxReportingDatasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    TotalGrossBase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalWithheld = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EvidenceCount = table.Column<int>(type: "integer", nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    FilingState = table.Column<int>(type: "integer", nullable: false),
                    HandoffReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReceiptReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneratedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FilingUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FilingUpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxReportingDatasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxObligationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerPayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerStatementVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Jurisdiction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    GrossBase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RatePercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    Rounding = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    TaxCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    StayIdsJson = table.Column<string>(type: "text", nullable: true),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    ReversesEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxObligationEvidence", x => x.Id);
                    table.CheckConstraint("CK_TaxObligationEvidence_Decision", "(\"Decision\" = 1 AND \"Amount\" IS NOT NULL) OR (\"Decision\" = 2 AND \"Amount\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TaxObligationEvidence_OwnerPayouts_OwnerPayoutId",
                        column: x => x.OwnerPayoutId,
                        principalTable: "OwnerPayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxObligationEvidence_OwnerStatementVersions_OwnerStatement~",
                        column: x => x.OwnerStatementVersionId,
                        principalTable: "OwnerStatementVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxObligationEvidence_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxObligationEvidence_TaxObligationEvidence_ReversesEvidenc~",
                        column: x => x.ReversesEvidenceId,
                        principalTable: "TaxObligationEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPayouts_OrgId_PropertyOwnerId_OwnerStatementVersionId",
                table: "OwnerPayouts",
                columns: new[] { "OrgId", "PropertyOwnerId", "OwnerStatementVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPayouts_OwnerStatementVersionId",
                table: "OwnerPayouts",
                column: "OwnerStatementVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPayouts_PropertyOwnerId",
                table: "OwnerPayouts",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerPayouts_OrgId_IdempotencyKey",
                table: "OwnerPayouts",
                columns: new[] { "OrgId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxObligationEvidence_OrgId_PeriodYear_PeriodMonth",
                table: "TaxObligationEvidence",
                columns: new[] { "OrgId", "PeriodYear", "PeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxObligationEvidence_OwnerPayoutId",
                table: "TaxObligationEvidence",
                column: "OwnerPayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxObligationEvidence_OwnerStatementVersionId",
                table: "TaxObligationEvidence",
                column: "OwnerStatementVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxObligationEvidence_PropertyOwnerId",
                table: "TaxObligationEvidence",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_TaxObligationEvidence_OrgId_IdempotencyKey",
                table: "TaxObligationEvidence",
                columns: new[] { "OrgId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_TaxObligationEvidence_ReversesEvidenceId",
                table: "TaxObligationEvidence",
                column: "ReversesEvidenceId",
                unique: true,
                filter: "\"ReversesEvidenceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_TaxReportingDatasets_OrgId_Kind_Year",
                table: "TaxReportingDatasets",
                columns: new[] { "OrgId", "Kind", "Year" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxObligationEvidence");

            migrationBuilder.DropTable(
                name: "TaxReportingDatasets");

            migrationBuilder.DropTable(
                name: "OwnerPayouts");

            migrationBuilder.DropColumn(
                name: "FilingState",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "FilingUpdatedAt",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "FilingUpdatedByUserId",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "HandoffReference",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "ReceiptReference",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "F24Records");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "F24Records");
        }
    }
}
