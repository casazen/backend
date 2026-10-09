using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrgActivityEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    When = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Area = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SubjectType = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgActivityEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgActivityEntries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrgActivityEntries_OrgId_Type",
                table: "OrgActivityEntries",
                columns: new[] { "OrgId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OrgActivityEntries_OrgId_When",
                table: "OrgActivityEntries",
                columns: new[] { "OrgId", "When" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OrgActivityEntries_When",
                table: "OrgActivityEntries",
                column: "When");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrgActivityEntries");
        }
    }
}
