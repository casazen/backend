using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComuniIstat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComuneIstatCodesJson",
                table: "SupplierProfiles",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "ComuneIstatCode",
                table: "Properties",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegionCode",
                table: "Properties",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ComuneImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ReferenceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    InsertedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    UnchangedCount = table.Column<int>(type: "integer", nullable: false),
                    DeactivatedCount = table.Column<int>(type: "integer", nullable: false),
                    IsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComuneImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Comuni",
                columns: table => new
                {
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    CadastralCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SearchText = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ProvinceCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    RegionIstatCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    RegionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comuni", x => x.IstatCode);
                    table.ForeignKey(
                        name: "FK_Comuni_ComuneImports_SourceImportId",
                        column: x => x.SourceImportId,
                        principalTable: "ComuneImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ComuneIstatCode",
                table: "Properties",
                column: "ComuneIstatCode");

            migrationBuilder.CreateIndex(
                name: "IX_ComuneImports_ImportedAt",
                table: "ComuneImports",
                column: "ImportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComuneImports_ReferenceDate",
                table: "ComuneImports",
                column: "ReferenceDate");

            migrationBuilder.CreateIndex(
                name: "IX_Comuni_CadastralCode_Active",
                table: "Comuni",
                column: "CadastralCode",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_Comuni_NormalizedName",
                table: "Comuni",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Comuni_ProvinceCode",
                table: "Comuni",
                column: "ProvinceCode");

            migrationBuilder.CreateIndex(
                name: "IX_Comuni_RegionIstatCode",
                table: "Comuni",
                column: "RegionIstatCode");

            migrationBuilder.CreateIndex(
                name: "IX_Comuni_SourceImportId",
                table: "Comuni",
                column: "SourceImportId");

            // SU-04: the SEO pages and signup attributions stored with the four wrong codes of the removed hardcoded registry
            // are moved to the official ISTAT codes (AddComuniIstat.Data.cs).
            ApplyData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RevertData(migrationBuilder);

            migrationBuilder.DropTable(
                name: "Comuni");

            migrationBuilder.DropTable(
                name: "ComuneImports");

            migrationBuilder.DropIndex(
                name: "IX_Properties_ComuneIstatCode",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ComuneIstatCodesJson",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "ComuneIstatCode",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "RegionCode",
                table: "Properties");
        }
    }
}
