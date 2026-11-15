using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyCleaningSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyCleaningSetups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CleanerKind = table.Column<int>(type: "integer", nullable: false),
                    SupplierOrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    StaffUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PriceCents = table.Column<int>(type: "integer", nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyCleaningSetups", x => x.Id);
                    table.CheckConstraint("CK_PropertyCleaningSetups_Cleaner", "(\"CleanerKind\" = 1 AND \"SupplierOrgId\" IS NOT NULL AND \"StaffUserId\" IS NULL AND \"PriceCents\" IS NOT NULL AND \"PriceCents\" BETWEEN 1 AND 10000000) OR (\"CleanerKind\" = 2 AND \"StaffUserId\" IS NOT NULL AND \"SupplierOrgId\" IS NULL AND (\"PriceCents\" IS NULL OR \"PriceCents\" BETWEEN 1 AND 10000000))");
                    table.CheckConstraint("CK_PropertyCleaningSetups_Duration", "\"DurationMinutes\" IS NULL OR \"DurationMinutes\" BETWEEN 5 AND 1440");
                    table.ForeignKey(
                        name: "FK_PropertyCleaningSetups_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyCleaningSetups_Orgs_SupplierOrgId",
                        column: x => x.SupplierOrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyCleaningSetups_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StayCleanings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CleanerKind = table.Column<int>(type: "integer", nullable: false),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    InternalActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayCleanings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayCleanings_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayCleanings_InternalActivities_InternalActivityId",
                        column: x => x.InternalActivityId,
                        principalTable: "InternalActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StayCleanings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayCleanings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayCleanings_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyCleaningSetups_OrgId",
                table: "PropertyCleaningSetups",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyCleaningSetups_SupplierOrgId",
                table: "PropertyCleaningSetups",
                column: "SupplierOrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyCleaningSetups_PropertyId",
                table: "PropertyCleaningSetups",
                column: "PropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayCleanings_BookingId",
                table: "StayCleanings",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_StayCleanings_InternalActivityId",
                table: "StayCleanings",
                column: "InternalActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_StayCleanings_PropertyId",
                table: "StayCleanings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_StayCleanings_ServiceRequestId",
                table: "StayCleanings",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "UIX_StayCleanings_OrgId_BookingId",
                table: "StayCleanings",
                columns: new[] { "OrgId", "BookingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyCleaningSetups");

            migrationBuilder.DropTable(
                name: "StayCleanings");
        }
    }
}
