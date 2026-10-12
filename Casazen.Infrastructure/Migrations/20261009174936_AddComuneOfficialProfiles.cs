using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Versioned official MEF <c>nuova_at</c> profiles of comuni (tourist-tax acts). Platform reference data, no org.
    /// </summary>
    public partial class AddComuneOfficialProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComuneOfficialProfiles",
                columns: table => new
                {
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastEnsuredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComuneOfficialProfiles", x => x.IstatCode);
                });

            migrationBuilder.CreateTable(
                name: "ComuneOfficialProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PdfSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IndexUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Authority = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MefPublishedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    ExtractJson = table.Column<string>(type: "jsonb", nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComuneOfficialProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComuneOfficialProfileVersions_ComuneOfficialProfiles_IstatC~",
                        column: x => x.IstatCode,
                        principalTable: "ComuneOfficialProfiles",
                        principalColumn: "IstatCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComuneOfficialProfileVersions_IstatCode_Current",
                table: "ComuneOfficialProfileVersions",
                column: "IstatCode",
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "IX_ComuneOfficialProfileVersions_IstatCode_VersionNumber",
                table: "ComuneOfficialProfileVersions",
                columns: new[] { "IstatCode", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComuneOfficialProfileVersions");

            migrationBuilder.DropTable(
                name: "ComuneOfficialProfiles");
        }
    }
}
