using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyComplianceStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ComplianceCompletedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ComplianceStatus",
                table: "Properties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SafetyChecklistJson",
                table: "Properties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutReminderJobId",
                table: "Bookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckoutWizardStartedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OrgId_ComplianceStatus",
                table: "Properties",
                columns: new[] { "OrgId", "ComplianceStatus" });

            migrationBuilder.Sql(
                """
                UPDATE "Properties"
                SET "ComplianceStatus" = 1
                WHERE "IsActive" = true AND "CinCode" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Properties_OrgId_ComplianceStatus",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CheckoutWizardStartedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CheckoutReminderJobId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SafetyChecklistJson",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ComplianceStatus",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ComplianceCompletedAt",
                table: "Properties");
        }
    }
}
