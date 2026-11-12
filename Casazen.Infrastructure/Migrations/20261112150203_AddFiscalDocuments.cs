using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalDocumentLines");

            migrationBuilder.DropTable(
                name: "FiscalNumberSequences");

            migrationBuilder.DropTable(
                name: "FiscalDocuments");
        }
    }
}
