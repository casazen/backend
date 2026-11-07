using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCondominiumHistoricalImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CondominiumImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Format = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumImportBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumImportBatches_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumImportHouses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FiscalStartMonth = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CondominiumId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumImportHouses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumImportHouses_CondominiumImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "CondominiumImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumImportHouses_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CondominiumImportHouses_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CondominiumImportYears",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OrdinaryCents = table.Column<long>(type: "bigint", nullable: false),
                    ExtraordinaryCents = table.Column<long>(type: "bigint", nullable: false),
                    FinalCents = table.Column<long>(type: "bigint", nullable: false),
                    PriorCents = table.Column<long>(type: "bigint", nullable: false),
                    PaymentCents = table.Column<long>(type: "bigint", nullable: false),
                    SettlementCents = table.Column<long>(type: "bigint", nullable: false),
                    InstallmentCount = table.Column<int>(type: "integer", nullable: false),
                    InstallmentTotalCents = table.Column<long>(type: "bigint", nullable: false),
                    Cadence = table.Column<int>(type: "integer", nullable: true),
                    PaymentsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondominiumImportYears", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CondominiumImportYears_CondominiumImportHouses_HouseId",
                        column: x => x.HouseId,
                        principalTable: "CondominiumImportHouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CondominiumImportYears_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportBatches_OrgId_ContentHash",
                table: "CondominiumImportBatches",
                columns: new[] { "OrgId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportHouses_BatchId",
                table: "CondominiumImportHouses",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportHouses_OrgId_PropertyId",
                table: "CondominiumImportHouses",
                columns: new[] { "OrgId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportHouses_PropertyId",
                table: "CondominiumImportHouses",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportYears_HouseId",
                table: "CondominiumImportYears",
                column: "HouseId");

            migrationBuilder.CreateIndex(
                name: "IX_CondominiumImportYears_OrgId",
                table: "CondominiumImportYears",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CondominiumImportYears");

            migrationBuilder.DropTable(
                name: "CondominiumImportHouses");

            migrationBuilder.DropTable(
                name: "CondominiumImportBatches");
        }
    }
}
