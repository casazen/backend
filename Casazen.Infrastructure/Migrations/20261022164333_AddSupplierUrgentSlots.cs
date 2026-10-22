using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierUrgentSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UrgentEnabled",
                table: "SupplierSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UrgentFromMinute",
                table: "SupplierSettings",
                type: "integer",
                nullable: false,
                defaultValue: 420);

            migrationBuilder.AddColumn<int>(
                name: "UrgentSurchargeCents",
                table: "SupplierSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UrgentToMinute",
                table: "SupplierSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1320);

            migrationBuilder.AddColumn<bool>(
                name: "UrgentEnabled",
                table: "SupplierServiceListings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UrgentSurchargeCents",
                table: "SupplierServiceListings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierSettings_UrgentSurchargeCents",
                table: "SupplierSettings",
                sql: "\"UrgentSurchargeCents\" BETWEEN 0 AND 50000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierSettings_UrgentWindow",
                table: "SupplierSettings",
                sql: "\"UrgentFromMinute\" >= 0 AND \"UrgentToMinute\" <= 1440 AND \"UrgentFromMinute\" < \"UrgentToMinute\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierServiceListings_UrgentSurchargeCents",
                table: "SupplierServiceListings",
                sql: "\"UrgentSurchargeCents\" IS NULL OR \"UrgentSurchargeCents\" BETWEEN 0 AND 50000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierSettings_UrgentSurchargeCents",
                table: "SupplierSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierSettings_UrgentWindow",
                table: "SupplierSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierServiceListings_UrgentSurchargeCents",
                table: "SupplierServiceListings");

            migrationBuilder.DropColumn(
                name: "UrgentEnabled",
                table: "SupplierSettings");

            migrationBuilder.DropColumn(
                name: "UrgentFromMinute",
                table: "SupplierSettings");

            migrationBuilder.DropColumn(
                name: "UrgentSurchargeCents",
                table: "SupplierSettings");

            migrationBuilder.DropColumn(
                name: "UrgentToMinute",
                table: "SupplierSettings");

            migrationBuilder.DropColumn(
                name: "UrgentEnabled",
                table: "SupplierServiceListings");

            migrationBuilder.DropColumn(
                name: "UrgentSurchargeCents",
                table: "SupplierServiceListings");
        }
    }
}
