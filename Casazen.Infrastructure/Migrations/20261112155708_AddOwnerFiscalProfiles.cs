using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerFiscalProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerFiscalProfiles");

            migrationBuilder.DropTable(
                name: "OwnerAccountingPolicies");
        }
    }
}
