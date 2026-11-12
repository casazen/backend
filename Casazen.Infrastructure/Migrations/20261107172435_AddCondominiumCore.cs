using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCondominiumCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Condominiums",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AdministratorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AdministratorEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AdministratorPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalStartMonth = table.Column<int>(type: "integer", nullable: false, defaultValue: 6),
                    CreatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Condominiums", x => x.Id);
                    table.CheckConstraint("CK_Condominiums_FiscalStartMonth", "\"FiscalStartMonth\" BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_Condominiums_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumPeriods", x => x.Id);
                    table.CheckConstraint("CK_CondominiumPeriods_Dates", "\"EndDate\" >= \"StartDate\"");
                    table.ForeignKey(
                        name: "FK_CondominiumPeriods_Condominiums_CondominiumId",
                        column: x => x.CondominiumId,
                        principalTable: "Condominiums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumPeriods_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Millesimi = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumUnits_Condominiums_CondominiumId",
                        column: x => x.CondominiumId,
                        principalTable: "Condominiums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumUnits_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CondominiumUnits_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumInstallments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OrdinaryAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PriorSettlementAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExtraordinaryAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumInstallments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumInstallments_CondominiumPeriods_CondominiumPerio~",
                        column: x => x.CondominiumPeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumInstallments_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumPriorBalances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcePeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DecidedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumPriorBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumPriorBalances_CondominiumPeriods_CondominiumPeri~",
                        column: x => x.CondominiumPeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumPriorBalances_CondominiumPeriods_SourcePeriodId",
                        column: x => x.SourcePeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CondominiumPriorBalances_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumStatements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumStatements_CondominiumPeriods_CondominiumPeriodId",
                        column: x => x.CondominiumPeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumStatements_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumInstallmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CondominiumPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriorBalanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BankMovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalSourceHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumPayments_CondominiumInstallments_CondominiumInst~",
                        column: x => x.CondominiumInstallmentId,
                        principalTable: "CondominiumInstallments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CondominiumPayments_CondominiumPeriods_CondominiumPeriodId",
                        column: x => x.CondominiumPeriodId,
                        principalTable: "CondominiumPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumPayments_CondominiumPriorBalances_PriorBalanceId",
                        column: x => x.PriorBalanceId,
                        principalTable: "CondominiumPriorBalances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CondominiumPayments_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumExpenseLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    CondominiumUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CompetenceStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CompetenceEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpenseNature = table.Column<int>(type: "integer", nullable: false),
                    TenantRecoverability = table.Column<int>(type: "integer", nullable: false),
                    RecoverablePercentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    RecoverableAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ClassificationSource = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ReviewStatus = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumExpenseLines", x => x.Id);
                    table.CheckConstraint("CK_CondominiumExpenseLines_Confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
                    table.ForeignKey(
                        name: "FK_CondominiumExpenseLines_CondominiumStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "CondominiumStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumExpenseLines_CondominiumUnits_CondominiumUnitId",
                        column: x => x.CondominiumUnitId,
                        principalTable: "CondominiumUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CondominiumExpenseLines_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumExpenseLines_CondominiumUnitId",
                table: "CondominiumExpenseLines",
                column: "CondominiumUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumExpenseLines_OrgId",
                table: "CondominiumExpenseLines",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumExpenseLines_StatementId",
                table: "CondominiumExpenseLines",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumInstallments_CondominiumPeriodId_SlotIndex",
                table: "CondominiumInstallments",
                columns: new[] { "CondominiumPeriodId", "SlotIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumInstallments_OrgId",
                table: "CondominiumInstallments",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPayments_CondominiumInstallmentId",
                table: "CondominiumPayments",
                column: "CondominiumInstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPayments_CondominiumPeriodId",
                table: "CondominiumPayments",
                column: "CondominiumPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPayments_OrgId_ExternalSourceHash",
                table: "CondominiumPayments",
                columns: new[] { "OrgId", "ExternalSourceHash" },
                unique: true,
                filter: "\"ExternalSourceHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPayments_PriorBalanceId",
                table: "CondominiumPayments",
                column: "PriorBalanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPeriods_CondominiumId_Label",
                table: "CondominiumPeriods",
                columns: new[] { "CondominiumId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPeriods_OrgId",
                table: "CondominiumPeriods",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPriorBalances_CondominiumPeriodId",
                table: "CondominiumPriorBalances",
                column: "CondominiumPeriodId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPriorBalances_OrgId",
                table: "CondominiumPriorBalances",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumPriorBalances_SourcePeriodId",
                table: "CondominiumPriorBalances",
                column: "SourcePeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_Condominiums_OrgId",
                table: "Condominiums",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumStatements_CondominiumPeriodId_Kind",
                table: "CondominiumStatements",
                columns: new[] { "CondominiumPeriodId", "Kind" },
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumStatements_CondominiumPeriodId_Kind_RevisionNumb~",
                table: "CondominiumStatements",
                columns: new[] { "CondominiumPeriodId", "Kind", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumStatements_OrgId",
                table: "CondominiumStatements",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumUnits_CondominiumId",
                table: "CondominiumUnits",
                column: "CondominiumId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumUnits_OrgId_PropertyId",
                table: "CondominiumUnits",
                columns: new[] { "OrgId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumUnits_PropertyId",
                table: "CondominiumUnits",
                column: "PropertyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CondominiumExpenseLines");

            migrationBuilder.DropTable(
                name: "CondominiumPayments");

            migrationBuilder.DropTable(
                name: "CondominiumStatements");

            migrationBuilder.DropTable(
                name: "CondominiumUnits");

            migrationBuilder.DropTable(
                name: "CondominiumInstallments");

            migrationBuilder.DropTable(
                name: "CondominiumPriorBalances");

            migrationBuilder.DropTable(
                name: "CondominiumPeriods");

            migrationBuilder.DropTable(
                name: "Condominiums");
        }
    }
}
