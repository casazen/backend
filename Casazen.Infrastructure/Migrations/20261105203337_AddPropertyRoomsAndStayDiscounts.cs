using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyRoomsAndStayDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChildrenPolicy",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PropertyRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    BedsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyRooms", x => x.Id);
                    table.CheckConstraint("CK_PropertyRooms_Position", "\"Position\" >= 0");
                    table.CheckConstraint("CK_PropertyRooms_Type", "\"Type\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_PropertyRooms_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyRooms_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyStayDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinNights = table.Column<int>(type: "integer", nullable: false),
                    PercentOff = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyStayDiscounts", x => x.Id);
                    table.CheckConstraint("CK_PropertyStayDiscounts_MinNights", "\"MinNights\" BETWEEN 2 AND 365");
                    table.CheckConstraint("CK_PropertyStayDiscounts_PercentOff", "\"PercentOff\" > 0 AND \"PercentOff\" <= 100");
                    table.ForeignKey(
                        name: "FK_PropertyStayDiscounts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyStayDiscounts_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRooms_OrgId",
                table: "PropertyRooms",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRooms_PropertyId_Position",
                table: "PropertyRooms",
                columns: new[] { "PropertyId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyStayDiscounts_OrgId",
                table: "PropertyStayDiscounts",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyStayDiscounts_PropertyId_MinNights",
                table: "PropertyStayDiscounts",
                columns: new[] { "PropertyId", "MinNights" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyRooms");

            migrationBuilder.DropTable(
                name: "PropertyStayDiscounts");

            migrationBuilder.DropColumn(
                name: "ChildrenPolicy",
                table: "Properties");
        }
    }
}
