using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSwissNotificationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwissNotificationDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrivalDay = table.Column<DateOnly>(type: "date", nullable: false),
                    DeclaredByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeclaredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "portal"),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    RegimeCode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwissNotificationDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwissNotificationDeclarations_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SwissNotificationDeclarations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SwissNotificationDeclarations_SwissComplianceReceipts_Recei~",
                        column: x => x.ReceiptId,
                        principalTable: "SwissComplianceReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SwissNotificationFileEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrivalDay = table.Column<DateOnly>(type: "date", nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwissNotificationFileEvents", x => x.Id);
                    table.CheckConstraint("CK_SwissNotificationFileEvents_Format", "\"Format\" IN ('csv', 'xml', 'register', 'groupSheet')");
                    table.CheckConstraint("CK_SwissNotificationFileEvents_RowCount", "\"RowCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_SwissNotificationFileEvents_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SwissNotificationFileEvents_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwissNotificationDeclarations_OrgId",
                table: "SwissNotificationDeclarations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_SwissNotificationDeclarations_ReceiptId",
                table: "SwissNotificationDeclarations",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "UX_SwissNotificationDeclarations_Property_Day",
                table: "SwissNotificationDeclarations",
                columns: new[] { "PropertyId", "ArrivalDay" },
                unique: true,
                filter: "\"RegimeCode\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SwissNotificationDeclarations_Property_Day_Regime",
                table: "SwissNotificationDeclarations",
                columns: new[] { "PropertyId", "ArrivalDay", "RegimeCode" },
                unique: true,
                filter: "\"RegimeCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SwissNotificationFileEvents_OrgId",
                table: "SwissNotificationFileEvents",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_SwissNotificationFileEvents_PropertyId_ArrivalDay_CreatedAt",
                table: "SwissNotificationFileEvents",
                columns: new[] { "PropertyId", "ArrivalDay", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwissNotificationDeclarations");

            migrationBuilder.DropTable(
                name: "SwissNotificationFileEvents");
        }
    }
}
