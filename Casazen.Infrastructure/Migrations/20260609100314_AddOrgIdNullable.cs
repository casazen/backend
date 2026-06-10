using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "Properties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "LeaseContracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Orgs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlanTier = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ThemeColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StripeCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripeConnectedAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orgs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrgId",
                table: "Users",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OrgId",
                table: "Properties",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrgId",
                table: "Payments",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseContracts_OrgId",
                table: "LeaseContracts",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrgId",
                table: "Bookings",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_Slug",
                table: "Orgs",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orgs");

            migrationBuilder.DropIndex(
                name: "IX_Users_OrgId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Properties_OrgId",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OrgId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_LeaseContracts_OrgId",
                table: "LeaseContracts");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OrgId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "Bookings");
        }
    }
}
