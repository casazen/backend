using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierAgenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierBusyWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalUid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBusyWindows", x => x.Id);
                    table.CheckConstraint("CK_SupplierBusyWindows_Interval", "\"StartUtc\" < \"EndUtc\"");
                    table.ForeignKey(
                        name: "FK_SupplierBusyWindows_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierSettings",
                columns: table => new
                {
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BufferMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxJobsPerDay = table.Column<int>(type: "integer", nullable: false),
                    MinNoticeHours = table.Column<int>(type: "integer", nullable: false),
                    HorizonDays = table.Column<int>(type: "integer", nullable: false),
                    SlotStepMinutes = table.Column<int>(type: "integer", nullable: false),
                    ParallelJobs = table.Column<int>(type: "integer", nullable: false),
                    RespondWithinMinutes = table.Column<int>(type: "integer", nullable: false),
                    OnlineBookingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HoursConfiguredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AutoAcceptRegulars = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyNewRequests = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyDayBeforeReminder = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyPaymentMarked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSettings", x => x.OrgId);
                    table.CheckConstraint("CK_SupplierSettings_BufferMinutes", "\"BufferMinutes\" BETWEEN 0 AND 240");
                    table.CheckConstraint("CK_SupplierSettings_HorizonDays", "\"HorizonDays\" BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_SupplierSettings_MaxJobsPerDay", "\"MaxJobsPerDay\" BETWEEN 1 AND 50");
                    table.CheckConstraint("CK_SupplierSettings_MinNoticeHours", "\"MinNoticeHours\" BETWEEN 0 AND 720");
                    table.CheckConstraint("CK_SupplierSettings_ParallelJobs", "\"ParallelJobs\" BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_SupplierSettings_RespondWithinMinutes", "\"RespondWithinMinutes\" BETWEEN 15 AND 10080");
                    table.CheckConstraint("CK_SupplierSettings_SlotStepMinutes", "\"SlotStepMinutes\" BETWEEN 15 AND 240");
                    table.ForeignKey(
                        name: "FK_SupplierSettings_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierTimeOff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierTimeOff", x => x.Id);
                    table.CheckConstraint("CK_SupplierTimeOff_Dates", "\"FromDate\" <= \"ToDate\"");
                    table.ForeignKey(
                        name: "FK_SupplierTimeOff_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierWorkingHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weekday = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndMinute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierWorkingHours", x => x.Id);
                    table.CheckConstraint("CK_SupplierWorkingHours_Minutes", "\"StartMinute\" >= 0 AND \"StartMinute\" < \"EndMinute\" AND \"EndMinute\" <= 1440");
                    table.CheckConstraint("CK_SupplierWorkingHours_Weekday", "\"Weekday\" BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_SupplierWorkingHours_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBusyWindows_OrgId_StartUtc",
                table: "SupplierBusyWindows",
                columns: new[] { "OrgId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTimeOff_OrgId_FromDate",
                table: "SupplierTimeOff",
                columns: new[] { "OrgId", "FromDate" });

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute",
                table: "SupplierWorkingHours",
                columns: new[] { "OrgId", "Weekday", "StartMinute" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierBusyWindows");

            migrationBuilder.DropTable(
                name: "SupplierSettings");

            migrationBuilder.DropTable(
                name: "SupplierTimeOff");

            migrationBuilder.DropTable(
                name: "SupplierWorkingHours");
        }
    }
}
