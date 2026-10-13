using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierFreeCalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierFreeCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UidHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IsAllDay = table.Column<bool>(type: "boolean", nullable: false),
                    Recurring = table.Column<bool>(type: "boolean", nullable: false),
                    NextStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Occurrences = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierFreeCalendarEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierFreeCalendarEvents_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFreeCalendarEvents_OrgId_Decision_NextStartUtc",
                table: "SupplierFreeCalendarEvents",
                columns: new[] { "OrgId", "Decision", "NextStartUtc" });

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierFreeCalendarEvents_OrgId_DecisionKey",
                table: "SupplierFreeCalendarEvents",
                columns: new[] { "OrgId", "DecisionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierFreeCalendarEvents");
        }
    }
}
