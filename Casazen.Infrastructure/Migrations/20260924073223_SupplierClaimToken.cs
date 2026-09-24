using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupplierClaimToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimTokenExpiresAt",
                table: "SupplierProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimTokenHash",
                table: "SupplierProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierProfiles_ClaimTokenHash",
                table: "SupplierProfiles",
                column: "ClaimTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_SupplierProfiles_ClaimTokenHash",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "ClaimTokenExpiresAt",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "ClaimTokenHash",
                table: "SupplierProfiles");
        }
    }
}
