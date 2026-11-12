using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalDocumentsAndOwnerAccounting : Migration
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

            migrationBuilder.AddColumn<string>(
                name: "ComplianceStatus",
                table: "AgencyInvoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "legacy_unverified");

            migrationBuilder.CreateTable(
                name: "FiscalDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: true),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RecipientKind = table.Column<int>(type: "integer", nullable: true),
                    RecipientName = table.Column<string>(type: "text", nullable: true),
                    RecipientFiscalCode = table.Column<string>(type: "text", nullable: true),
                    RecipientVatNumber = table.Column<string>(type: "text", nullable: true),
                    RecipientAddressStreet = table.Column<string>(type: "text", nullable: true),
                    RecipientAddressCivic = table.Column<string>(type: "text", nullable: true),
                    RecipientPostalCode = table.Column<string>(type: "text", nullable: true),
                    RecipientCity = table.Column<string>(type: "text", nullable: true),
                    RecipientProvince = table.Column<string>(type: "text", nullable: true),
                    RecipientCountryCode = table.Column<string>(type: "text", nullable: true),
                    RecipientSdiCode = table.Column<string>(type: "text", nullable: true),
                    RecipientPec = table.Column<string>(type: "text", nullable: true),
                    TaxableBase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    StampDuty = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    VatNatureCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    PaymentState = table.Column<int>(type: "integer", nullable: false),
                    SdiStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SdiTransmissionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SdiErrorCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SdiErrorDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SdiStatusUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FatturaPaXml = table.Column<string>(type: "text", nullable: true),
                    OctorateAccommodationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OctorateDocumentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OctorateReservationId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OctoratePdfAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    CreditedDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacesDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDocuments", x => x.Id);
                    table.CheckConstraint("CK_FiscalDocuments_SdiStatus", "\"SdiStatus\" IN ('not_applicable', 'to_send', 'submitted', 'delivered', 'rejected', 'manual_declared', 'unknown')");
                    table.CheckConstraint("CK_FiscalDocuments_SequenceNumber", "\"SequenceNumber\" IS NULL OR \"SequenceNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_FiscalDocuments_CreditedDocumentId",
                        column: x => x.CreditedDocumentId,
                        principalTable: "FiscalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_FiscalDocuments_ReplacesDocumentId",
                        column: x => x.ReplacesDocumentId,
                        principalTable: "FiscalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalNumberSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Series = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastNumber = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalNumberSequences", x => x.Id);
                    table.CheckConstraint("CK_FiscalNumberSequences_LastNumber", "\"LastNumber\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "OwnerAccountingPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Jurisdiction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerAccountingPolicies", x => x.Id);
                    table.CheckConstraint("CK_OwnerAccountingPolicies_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
                });

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
                name: "FiscalDocumentLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    VatNatureCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDocumentLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalDocumentLines_FiscalDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "FiscalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerFiscalProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    EntityKind = table.Column<int>(type: "integer", nullable: false),
                    LegalName = table.Column<string>(type: "text", nullable: true),
                    TaxResidenceCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    LegalAddress = table.Column<string>(type: "text", nullable: true),
                    FiscalCode = table.Column<string>(type: "text", nullable: true),
                    VatNumber = table.Column<string>(type: "text", nullable: true),
                    ForeignTaxId = table.Column<string>(type: "text", nullable: true),
                    SettlementCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    BankAccountReference = table.Column<string>(type: "text", nullable: true),
                    MandateRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MandateFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    MandateTo = table.Column<DateOnly>(type: "date", nullable: true),
                    RevenueRecognitionRule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExpenseCommissionModel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkingCapitalRule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TaxJurisdiction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    AccountingPolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerFiscalProfiles", x => x.Id);
                    table.CheckConstraint("CK_OwnerFiscalProfiles_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_OwnerFiscalProfiles_OwnerAccountingPolicies_AccountingPolic~",
                        column: x => x.AccountingPolicyId,
                        principalTable: "OwnerAccountingPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerFiscalProfiles_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerExternalDocumentReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerStatementVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DocumentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RemovalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerExternalDocumentReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerExternalDocumentReferences_OwnerStatementVersions_Owne~",
                        column: x => x.OwnerStatementVersionId,
                        principalTable: "OwnerStatementVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerExternalDocumentReferences_PropertyOwners_PropertyOwne~",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                name: "UIX_FiscalDocumentLines_DocumentId_LineNumber",
                table: "FiscalDocumentLines",
                columns: new[] { "DocumentId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_BookingId",
                table: "FiscalDocuments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_CreditedDocumentId",
                table: "FiscalDocuments",
                column: "CreditedDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_OrgId_BookingId",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_OrgId_IssueDate",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_ReplacesDocumentId",
                table: "FiscalDocuments",
                column: "ReplacesDocumentId");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalDocuments_OrgId_OctorateDocumentId",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "OctorateDocumentId" },
                unique: true,
                filter: "\"OctorateDocumentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalDocuments_OrgId_Series_SequenceNumber",
                table: "FiscalDocuments",
                columns: new[] { "OrgId", "Series", "SequenceNumber" },
                unique: true,
                filter: "\"SequenceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_FiscalNumberSequences_OrgId_Series",
                table: "FiscalNumberSequences",
                columns: new[] { "OrgId", "Series" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerAccountingPolicies_OrgId_Jurisdiction",
                table: "OwnerAccountingPolicies",
                columns: new[] { "OrgId", "Jurisdiction" });

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerAccountingPolicies_OrgId_RuleCode_Version",
                table: "OwnerAccountingPolicies",
                columns: new[] { "OrgId", "RuleCode", "Version" },
                unique: true);

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
                name: "IX_OwnerExternalDocumentReferences_OrgId_PropertyOwnerId",
                table: "OwnerExternalDocumentReferences",
                columns: new[] { "OrgId", "PropertyOwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerExternalDocumentReferences_OwnerStatementVersionId",
                table: "OwnerExternalDocumentReferences",
                column: "OwnerStatementVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerExternalDocumentReferences_PropertyOwnerId",
                table: "OwnerExternalDocumentReferences",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerExternalDocumentReferences_Live",
                table: "OwnerExternalDocumentReferences",
                columns: new[] { "OrgId", "OwnerStatementVersionId", "Provider", "ExternalId" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerFiscalProfiles_AccountingPolicyId",
                table: "OwnerFiscalProfiles",
                column: "AccountingPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerFiscalProfiles_PropertyOwnerId",
                table: "OwnerFiscalProfiles",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerFiscalProfiles_OrgId_PropertyOwnerId_ValidFrom",
                table: "OwnerFiscalProfiles",
                columns: new[] { "OrgId", "PropertyOwnerId", "ValidFrom" },
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
                name: "IX_OwnerStatementVersions_PropertyOwnerId",
                table: "OwnerStatementVersions",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStatementVersions_OrgId_Owner_Period_Version",
                table: "OwnerStatementVersions",
                columns: new[] { "OrgId", "PropertyOwnerId", "PeriodYear", "PeriodMonth", "VersionNumber" },
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
                name: "FiscalDocumentLines");

            migrationBuilder.DropTable(
                name: "FiscalNumberSequences");

            migrationBuilder.DropTable(
                name: "OwnerCharges");

            migrationBuilder.DropTable(
                name: "OwnerClosePeriods");

            migrationBuilder.DropTable(
                name: "OwnerExternalDocumentReferences");

            migrationBuilder.DropTable(
                name: "OwnerFiscalProfiles");

            migrationBuilder.DropTable(
                name: "OwnerFxRates");

            migrationBuilder.DropTable(
                name: "OwnerLedgerEntries");

            migrationBuilder.DropTable(
                name: "TaxObligationEvidence");

            migrationBuilder.DropTable(
                name: "TaxReportingDatasets");

            migrationBuilder.DropTable(
                name: "FiscalDocuments");

            migrationBuilder.DropTable(
                name: "OwnerAccountingPolicies");

            migrationBuilder.DropTable(
                name: "OwnerPayouts");

            migrationBuilder.DropTable(
                name: "OwnerStatementVersions");

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

            migrationBuilder.DropColumn(
                name: "ComplianceStatus",
                table: "AgencyInvoices");
        }
    }
}
