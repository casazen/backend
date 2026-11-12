using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Recurrence = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    AttachmentReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerCharges", x => x.Id);
                    table.CheckConstraint("CK_OwnerCharges_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_OwnerCharges_Validity", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.ForeignKey(
                        name: "FK_OwnerCharges_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerCharges_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerClosePeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsDirty = table.Column<bool>(type: "boolean", nullable: false),
                    DirtySince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ClosedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Difference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    WaiverReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WaivedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    WaivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetentionJurisdiction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    RetentionYears = table.Column<int>(type: "integer", nullable: true),
                    RetainUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LegalHold = table.Column<bool>(type: "boolean", nullable: false),
                    LegalHoldReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LegalHoldByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LegalHoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerClosePeriods", x => x.Id);
                    table.CheckConstraint("CK_OwnerClosePeriods_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_OwnerClosePeriods_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerFxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    QuoteCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerFxRates", x => x.Id);
                    table.CheckConstraint("CK_OwnerFxRates_Rate", "\"Rate\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "OwnerLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttachmentReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecognitionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    PropertyOwnershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    SharePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    OriginalCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SourceAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    StatementCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FxRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FxSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FxDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TaxCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RuleVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReversesEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_OwnerLedgerEntries_FxRate", "\"FxRate\" > 0");
                    table.CheckConstraint("CK_OwnerLedgerEntries_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_OwnerLedgerEntries_Share", "\"SharePercent\" >= 0 AND \"SharePercent\" <= 100");
                    table.ForeignKey(
                        name: "FK_OwnerLedgerEntries_OwnerLedgerEntries_ReversesEntryId",
                        column: x => x.ReversesEntryId,
                        principalTable: "OwnerLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerLedgerEntries_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerLedgerEntries_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerStatementVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CutoffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Revenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ChannelFees = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Charges = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Payments = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Corrections = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    EntriesSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PublishedSnapshot = table.Column<string>(type: "text", nullable: true),
                    PublishedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SupersedesVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededByVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerStatementVersions", x => x.Id);
                    table.CheckConstraint("CK_OwnerStatementVersions_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_OwnerStatementVersions_VersionNumber", "\"VersionNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_OwnerStatementVersions_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_OrgId_PropertyId_Status",
                table: "OwnerCharges",
                columns: new[] { "OrgId", "PropertyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_PropertyId",
                table: "OwnerCharges",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_PropertyOwnerId",
                table: "OwnerCharges",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerClosePeriods_PropertyOwnerId",
                table: "OwnerClosePeriods",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerClosePeriods_OrgId_Owner_Period",
                table: "OwnerClosePeriods",
                columns: new[] { "OrgId", "PropertyOwnerId", "PeriodYear", "PeriodMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerFxRates_OrgId_Pair_Date",
                table: "OwnerFxRates",
                columns: new[] { "OrgId", "BaseCurrency", "QuoteCurrency", "RateDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerLedgerEntries_OrgId_PropertyOwnerId_PeriodYear_PeriodM~",
                table: "OwnerLedgerEntries",
                columns: new[] { "OrgId", "PropertyOwnerId", "PeriodYear", "PeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerLedgerEntries_OrgId_SourceType_SourceId",
                table: "OwnerLedgerEntries",
                columns: new[] { "OrgId", "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerLedgerEntries_PropertyId",
                table: "OwnerLedgerEntries",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerLedgerEntries_PropertyOwnerId",
                table: "OwnerLedgerEntries",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerLedgerEntries_OrgId_IdempotencyKey",
                table: "OwnerLedgerEntries",
                columns: new[] { "OrgId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerLedgerEntries_ReversesEntryId",
                table: "OwnerLedgerEntries",
                column: "ReversesEntryId",
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStatementVersions_PropertyOwnerId",
                table: "OwnerStatementVersions",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStatementVersions_OrgId_Owner_Period_Version",
                table: "OwnerStatementVersions",
                columns: new[] { "OrgId", "PropertyOwnerId", "PeriodYear", "PeriodMonth", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerCharges");

            migrationBuilder.DropTable(
                name: "OwnerClosePeriods");

            migrationBuilder.DropTable(
                name: "OwnerFxRates");

            migrationBuilder.DropTable(
                name: "OwnerLedgerEntries");

            migrationBuilder.DropTable(
                name: "OwnerStatementVersions");
        }
    }
}
