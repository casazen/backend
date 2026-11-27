using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCondominiumSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CondominiumInstallmentId",
                table: "BankLinkPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantCondominiumSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementRevision = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    RecoverableFinalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AdvancesAccountedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PriorTenantBalanceAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SettlementAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    AllocationRule = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AbsorbedBySettlementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CalculationSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    SentToTenantAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCondominiumSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantCondominiumSettlements_CondominiumPeriods_Condominium~",
                        column: x => x.CondominiumPeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCondominiumSettlements_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCondominiumSettlements_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantExpenseAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantCondominiumSettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumExpenseLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rule = table.Column<int>(type: "integer", nullable: false),
                    OccupancyStart = table.Column<DateOnly>(type: "date", nullable: true),
                    OccupancyEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CalculationDetailsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantExpenseAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantExpenseAllocations_CondominiumExpenseLines_Condominiu~",
                        column: x => x.CondominiumExpenseLineId,
                        principalTable: "CondominiumExpenseLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExpenseAllocations_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExpenseAllocations_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExpenseAllocations_TenantCondominiumSettlements_Tenan~",
                        column: x => x.TenantCondominiumSettlementId,
                        principalTable: "TenantCondominiumSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerComponents_TenantSettlementId",
                table: "RentLedgerComponents",
                column: "TenantSettlementId",
                unique: true,
                filter: "\"TenantSettlementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankLinkPayments_CondominiumInstallmentId",
                table: "BankLinkPayments",
                column: "CondominiumInstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCondominiumSettlements_CondominiumPeriodId",
                table: "TenantCondominiumSettlements",
                column: "CondominiumPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCondominiumSettlements_LeaseContractId_StatementId",
                table: "TenantCondominiumSettlements",
                columns: new[] { "LeaseContractId", "StatementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantCondominiumSettlements_OrgId_CondominiumPeriodId",
                table: "TenantCondominiumSettlements",
                columns: new[] { "OrgId", "CondominiumPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExpenseAllocations_CondominiumExpenseLineId",
                table: "TenantExpenseAllocations",
                column: "CondominiumExpenseLineId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantExpenseAllocations_LeaseContractId",
                table: "TenantExpenseAllocations",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantExpenseAllocations_OrgId",
                table: "TenantExpenseAllocations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantExpenseAllocations_TenantCondominiumSettlementId",
                table: "TenantExpenseAllocations",
                column: "TenantCondominiumSettlementId");

            migrationBuilder.AddForeignKey(
                name: "FK_RentLedgerComponents_TenantCondominiumSettlements_TenantSet~",
                table: "RentLedgerComponents",
                column: "TenantSettlementId",
                principalTable: "TenantCondominiumSettlements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RentLedgerComponents_TenantCondominiumSettlements_TenantSet~",
                table: "RentLedgerComponents");

            migrationBuilder.DropTable(
                name: "TenantExpenseAllocations");

            migrationBuilder.DropTable(
                name: "TenantCondominiumSettlements");

            migrationBuilder.DropIndex(
                name: "IX_RentLedgerComponents_TenantSettlementId",
                table: "RentLedgerComponents");

            migrationBuilder.DropIndex(
                name: "IX_BankLinkPayments_CondominiumInstallmentId",
                table: "BankLinkPayments");

            migrationBuilder.DropColumn(
                name: "CondominiumInstallmentId",
                table: "BankLinkPayments");
        }
    }
}
