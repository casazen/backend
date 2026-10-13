using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupplierInviteTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "SupplierInviteRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierInviteRecords_TokenHash",
                table: "SupplierInviteRecords",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_SupplierInviteRecords_TokenHash",
                table: "SupplierInviteRecords");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "SupplierInviteRecords");
        }
    }
}
