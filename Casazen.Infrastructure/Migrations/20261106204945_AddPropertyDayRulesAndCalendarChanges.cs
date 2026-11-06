using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyDayRulesAndCalendarChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarChangeProcesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PropertyCount = table.Column<int>(type: "integer", nullable: false),
                    DayCount = table.Column<int>(type: "integer", nullable: false),
                    SummaryJson = table.Column<string>(type: "jsonb", nullable: false),
                    QuotaUnitsEstimated = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarChangeProcesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarChangeProcesses_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PropertyDayRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    MinNights = table.Column<int>(type: "integer", nullable: true),
                    MaxNights = table.Column<int>(type: "integer", nullable: true),
                    ClosedToArrival = table.Column<bool>(type: "boolean", nullable: false),
                    ClosedToDeparture = table.Column<bool>(type: "boolean", nullable: false),
                    MinAdvanceDays = table.Column<int>(type: "integer", nullable: true),
                    MaxAdvanceDays = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyDayRules", x => x.Id);
                    table.CheckConstraint("CK_PropertyDayRules_Values", "(\"Price\" IS NULL OR (\"Price\" >= 0.01 AND \"Price\" <= 100000)) AND (\"MinNights\" IS NULL OR (\"MinNights\" >= 1 AND \"MinNights\" <= 365)) AND (\"MaxNights\" IS NULL OR (\"MaxNights\" >= 1 AND \"MaxNights\" <= 365)) AND (\"MinNights\" IS NULL OR \"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\") AND (\"MinAdvanceDays\" IS NULL OR (\"MinAdvanceDays\" >= 0 AND \"MinAdvanceDays\" <= 730)) AND (\"MaxAdvanceDays\" IS NULL OR (\"MaxAdvanceDays\" >= 0 AND \"MaxAdvanceDays\" <= 730)) AND (\"MinAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")");
                    table.ForeignKey(
                        name: "FK_PropertyDayRules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyDayRules_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarChangeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    OctorateProcessId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarChangeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarChangeChunks_CalendarChangeProcesses_ProcessId",
                        column: x => x.ProcessId,
                        principalTable: "CalendarChangeProcesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarChangeChunks_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarChangeChunks_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarChangeChunks_OrgId_PropertyId_State",
                table: "CalendarChangeChunks",
                columns: new[] { "OrgId", "PropertyId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarChangeChunks_ProcessId",
                table: "CalendarChangeChunks",
                column: "ProcessId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarChangeChunks_PropertyId",
                table: "CalendarChangeChunks",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarChangeProcesses_OrgId_CreatedAt",
                table: "CalendarChangeProcesses",
                columns: new[] { "OrgId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarChangeProcesses_State_NextAttemptAt",
                table: "CalendarChangeProcesses",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDayRules_OrgId_PropertyId_StayDate",
                table: "PropertyDayRules",
                columns: new[] { "OrgId", "PropertyId", "StayDate" });

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyDayRules_PropertyId_StayDate",
                table: "PropertyDayRules",
                columns: new[] { "PropertyId", "StayDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarChangeChunks");

            migrationBuilder.DropTable(
                name: "PropertyDayRules");

            migrationBuilder.DropTable(
                name: "CalendarChangeProcesses");
        }
    }
}
