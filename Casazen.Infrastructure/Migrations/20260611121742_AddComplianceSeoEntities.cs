using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceSeoEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformAiBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthlyTokenCap = table.Column<long>(type: "bigint", nullable: false),
                    TokensUsedThisMonth = table.Column<long>(type: "bigint", nullable: false),
                    LastResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAiBudgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeoContentPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ComuneCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RegionCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PageType = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MetaDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LegalReviewStatus = table.Column<int>(type: "integer", nullable: false),
                    CounselRequired = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoContentPages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeoContentRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    AiModelTier = table.Column<int>(type: "integer", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceDataVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoContentRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeoContentRevisions_SeoContentPages_PageId",
                        column: x => x.PageId,
                        principalTable: "SeoContentPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[] { "admin.seo.read", 3 });

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentPages_ComuneCode_PageType",
                table: "SeoContentPages",
                columns: new[] { "ComuneCode", "PageType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentPages_LegalReviewStatus",
                table: "SeoContentPages",
                column: "LegalReviewStatus");

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentRevisions_PageId",
                table: "SeoContentRevisions",
                column: "PageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAiBudgets");

            migrationBuilder.DropTable(
                name: "SeoContentRevisions");

            migrationBuilder.DropTable(
                name: "SeoContentPages");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "admin.seo.read", 3 });
        }
    }
}
