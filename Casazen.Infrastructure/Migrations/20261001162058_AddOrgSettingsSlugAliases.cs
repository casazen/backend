using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgSettingsSlugAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContactEmailPublic",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OrgSlugAliases",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgSlugAliases", x => x.Slug);
                    table.ForeignKey(
                        name: "FK_OrgSlugAliases_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrgSlugAliases_OrgId",
                table: "OrgSlugAliases",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrgSlugAliases");

            migrationBuilder.DropColumn(
                name: "ContactEmailPublic",
                table: "Orgs");
        }
    }
}
