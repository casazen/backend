using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyFloorPlansAndPois : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyFloorPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    NameIt = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ImageWidth = table.Column<int>(type: "integer", nullable: true),
                    ImageHeight = table.Column<int>(type: "integer", nullable: true),
                    AreaSqm = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyFloorPlans", x => x.Id);
                    table.CheckConstraint("CK_PropertyFloorPlans_AreaSqm", "\"AreaSqm\" IS NULL OR \"AreaSqm\" BETWEEN 1 AND 9999");
                    table.CheckConstraint("CK_PropertyFloorPlans_Level", "\"Level\" BETWEEN -3 AND 60");
                    table.CheckConstraint("CK_PropertyFloorPlans_Position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_PropertyFloorPlans_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyFloorPlans_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyPois",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    NameIt = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Minutes = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    DistanceMeters = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyPois", x => x.Id);
                    table.CheckConstraint("CK_PropertyPois_Direction", "\"Direction\" BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_PropertyPois_DistanceMeters", "\"DistanceMeters\" BETWEEN 200 AND 200000");
                    table.CheckConstraint("CK_PropertyPois_Minutes", "\"Minutes\" BETWEEN 1 AND 600");
                    table.ForeignKey(
                        name: "FK_PropertyPois_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyPois_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFloorPlans_OrgId",
                table: "PropertyFloorPlans",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFloorPlans_PropertyId_Position",
                table: "PropertyFloorPlans",
                columns: new[] { "PropertyId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyPois_OrgId",
                table: "PropertyPois",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyPois_PropertyId_Position",
                table: "PropertyPois",
                columns: new[] { "PropertyId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyFloorPlans");

            migrationBuilder.DropTable(
                name: "PropertyPois");
        }
    }
}
