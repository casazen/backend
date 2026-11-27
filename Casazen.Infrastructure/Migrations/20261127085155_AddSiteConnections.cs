using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteConnectionCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteConnectionCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteConnectionCodes_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SiteConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteConnections_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteConnectionCodes_ExpiresAt",
                table: "SiteConnectionCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SiteConnectionCodes_OrgId",
                table: "SiteConnectionCodes",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_SiteConnectionCodes_CodeHash",
                table: "SiteConnectionCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_SiteConnections_OrgId_SiteUrl_Active",
                table: "SiteConnections",
                columns: new[] { "OrgId", "SiteUrl" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_SiteConnections_TokenHash",
                table: "SiteConnections",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteConnectionCodes");

            migrationBuilder.DropTable(
                name: "SiteConnections");
        }
    }
}
