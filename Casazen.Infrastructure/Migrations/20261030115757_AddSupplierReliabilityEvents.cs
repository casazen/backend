using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierReliabilityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierReliabilityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsBackup = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierReliabilityEvents", x => x.Id);
                    table.CheckConstraint("CK_SupplierReliabilityEvents_Kind", "\"Kind\" IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_SupplierReliabilityEvents_SupplierProfiles_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierReliabilityEvents_Supplier_OccurredAt",
                table: "SupplierReliabilityEvents",
                columns: new[] { "SupplierOrgId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierReliabilityEvents_Request",
                table: "SupplierReliabilityEvents",
                columns: new[] { "SupplierOrgId", "RequestId", "Kind" },
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierReliabilityEvents");
        }
    }
}
