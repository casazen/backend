using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerPortalOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerStayRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByOwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AutoApprove = table.Column<bool>(type: "boolean", nullable: false),
                    CalendarBlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlockChangeProcessId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncState = table.Column<int>(type: "integer", nullable: false),
                    BlockReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseChangeProcessId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseSyncState = table.Column<int>(type: "integer", nullable: false),
                    SyncErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SyncCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerStayRequests", x => x.Id);
                    table.CheckConstraint("CK_OwnerStayRequests_Dates", "\"EndDate\" > \"StartDate\" AND \"Nights\" >= 1");
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRequests_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerStayRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    YearlyNightAllowance = table.Column<int>(type: "integer", nullable: true),
                    MinAdvanceDays = table.Column<int>(type: "integer", nullable: false),
                    MaxAdvanceDays = table.Column<int>(type: "integer", nullable: true),
                    MinNights = table.Column<int>(type: "integer", nullable: false),
                    MaxNights = table.Column<int>(type: "integer", nullable: true),
                    ApprovalRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ActiveUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    BlackoutsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerStayRules", x => x.Id);
                    table.CheckConstraint("CK_OwnerStayRules_Advance", "\"MinAdvanceDays\" >= 0 AND (\"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")");
                    table.CheckConstraint("CK_OwnerStayRules_Allowance", "\"YearlyNightAllowance\" IS NULL OR \"YearlyNightAllowance\" BETWEEN 0 AND 366");
                    table.CheckConstraint("CK_OwnerStayRules_Nights", "\"MinNights\" >= 1 AND (\"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\")");
                    table.CheckConstraint("CK_OwnerStayRules_Period", "\"ActiveFrom\" IS NULL OR \"ActiveUntil\" IS NULL OR \"ActiveUntil\" >= \"ActiveFrom\"");
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerStayRules_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_CalendarBlockId",
                table: "OwnerStayRequests",
                column: "CalendarBlockId",
                unique: true,
                filter: "\"CalendarBlockId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_OrgId_PropertyOwnerId_StartDate",
                table: "OwnerStayRequests",
                columns: new[] { "OrgId", "PropertyOwnerId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_PropertyId_Status_StartDate",
                table: "OwnerStayRequests",
                columns: new[] { "PropertyId", "Status", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_PropertyOwnerId",
                table: "OwnerStayRequests",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRequests_Status_EndDate",
                table: "OwnerStayRequests",
                columns: new[] { "Status", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRules_OrgId",
                table: "OwnerStayRules",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerStayRules_PropertyOwnerId",
                table: "OwnerStayRules",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStayRules_Property",
                table: "OwnerStayRules",
                column: "PropertyId",
                unique: true,
                filter: "\"PropertyOwnerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerStayRules_Property_Owner",
                table: "OwnerStayRules",
                columns: new[] { "PropertyId", "PropertyOwnerId" },
                unique: true,
                filter: "\"PropertyOwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerStayRequests");

            migrationBuilder.DropTable(
                name: "OwnerStayRules");
        }
    }
}
