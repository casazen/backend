using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PropertyAddress = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckInLocation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CheckInToken = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    CheckInTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierJobs_SupplierProfiles_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierJobs_SupplierOrgId",
                table: "SupplierJobs",
                column: "SupplierOrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierJobs");
        }
    }
}
