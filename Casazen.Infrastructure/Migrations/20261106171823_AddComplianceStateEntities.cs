using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceStateEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComplianceAlertStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ReferenceKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    AlertCount = table.Column<int>(type: "integer", nullable: false),
                    LastAlertAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceAlertStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplianceAlertStates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplianceAlertStates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceMonthlySummaryLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    YearMonth = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecipientCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceMonthlySummaryLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplianceMonthlySummaryLogs_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceSendLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Sender = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceSendLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplianceSendLogEntries_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ComplianceSendLogEntries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplianceSendLogEntries_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualComplianceProofs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    FileKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UploadedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualComplianceProofs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualComplianceProofs_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualComplianceProofs_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceAlertStates_OrgId",
                table: "ComplianceAlertStates",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceAlertStates_PropertyId_Type_ReferenceKey",
                table: "ComplianceAlertStates",
                columns: new[] { "PropertyId", "Type", "ReferenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_ComplianceMonthlySummaryLogs_OrgId_YearMonth",
                table: "ComplianceMonthlySummaryLogs",
                columns: new[] { "OrgId", "YearMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceSendLogEntries_BookingId",
                table: "ComplianceSendLogEntries",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceSendLogEntries_OrgId_PropertyId_SentAt",
                table: "ComplianceSendLogEntries",
                columns: new[] { "OrgId", "PropertyId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceSendLogEntries_PropertyId",
                table: "ComplianceSendLogEntries",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualComplianceProofs_OrgId_PropertyId_Kind_Day",
                table: "ManualComplianceProofs",
                columns: new[] { "OrgId", "PropertyId", "Kind", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualComplianceProofs_PropertyId",
                table: "ManualComplianceProofs",
                column: "PropertyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplianceAlertStates");

            migrationBuilder.DropTable(
                name: "ComplianceMonthlySummaryLogs");

            migrationBuilder.DropTable(
                name: "ComplianceSendLogEntries");

            migrationBuilder.DropTable(
                name: "ManualComplianceProofs");
        }
    }
}
