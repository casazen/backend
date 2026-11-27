using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalAccountHolder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PortalAccountHolder",
                table: "OrgFiscalIssuerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "RechargePortalCommissions",
                table: "OrgFiscalIssuerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "PropertyChannelAccountHolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Holder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyChannelAccountHolders", x => x.Id);
                    table.CheckConstraint("CK_PropertyChannelAccountHolders_Holder", "\"Holder\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_PropertyChannelAccountHolders_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyChannelAccountHolders_OrgId",
                table: "PropertyChannelAccountHolders",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyChannelAccountHolders_PropertyId_Channel",
                table: "PropertyChannelAccountHolders",
                columns: new[] { "PropertyId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyChannelAccountHolders");

            migrationBuilder.DropColumn(
                name: "PortalAccountHolder",
                table: "OrgFiscalIssuerProfiles");

            migrationBuilder.DropColumn(
                name: "RechargePortalCommissions",
                table: "OrgFiscalIssuerProfiles");
        }
    }
}
