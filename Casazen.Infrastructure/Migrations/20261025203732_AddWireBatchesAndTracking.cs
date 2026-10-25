using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWireBatchesAndTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WireBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DebitAccount = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TransferCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    XmlContent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WireBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WireBatches_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WireBatchStatements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    WireBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerStatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WireBatchStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WireBatchStatements_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WireBatchStatements_OwnerStatements_OwnerStatementId",
                        column: x => x.OwnerStatementId,
                        principalTable: "OwnerStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WireBatchStatements_WireBatches_WireBatchId",
                        column: x => x.WireBatchId,
                        principalTable: "WireBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WireBatches_OrgId",
                table: "WireBatches",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchStatements_OrgId",
                table: "WireBatchStatements",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchStatements_OwnerStatementId",
                table: "WireBatchStatements",
                column: "OwnerStatementId");

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchStatements_WireBatchId",
                table: "WireBatchStatements",
                column: "WireBatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WireBatchStatements");

            migrationBuilder.DropTable(
                name: "WireBatches");
        }
    }
}
