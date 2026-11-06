using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTouristTaxRemittance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TouristTaxRemittances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comune = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ComuneKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReceiptStorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReceiptFileName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    ReceiptContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReceiptSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    RecordedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TouristTaxRemittances", x => x.Id);
                    table.CheckConstraint("CK_TouristTaxRemittances_PaidAmountPositive", "\"PaidAmount\" > 0");
                    table.CheckConstraint("CK_TouristTaxRemittances_PeriodOrder", "\"PeriodFrom\" <= \"PeriodTo\"");
                    table.ForeignKey(
                        name: "FK_TouristTaxRemittances_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TouristTaxRemittances_OrgId_ComuneKey_PeriodFrom_PeriodTo",
                table: "TouristTaxRemittances",
                columns: new[] { "OrgId", "ComuneKey", "PeriodFrom", "PeriodTo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TouristTaxRemittances");
        }
    }
}
