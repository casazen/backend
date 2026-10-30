using System;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// E02/F01/US02/T01: Swiss municipality table and import. Creates the SwissMunicipalities table for CH properties
    /// and seeds it with official data from Ufficio federale di statistica (BFS/OFS/UST). Adds SwissMunicipalityCode FK
    /// to Property for Swiss properties (Country = 'CH'). Ref: ADR-001-R9, NF-02.
    /// </summary>
    /// <remarks>
    /// Documentation: docs/compliance/swiss-municipalities.md
    /// </remarks>
    public partial class AddSwissMunicipality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SwissMunicipalityCode",
                table: "Properties",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SwissMunicipalities",
                columns: table => new
                {
                    MunicipalityCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CantonCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    OfficialName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwissMunicipalities", x => x.MunicipalityCode);
                });

            SwissMunicipalitySeed.Insert(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            SwissMunicipalitySeed.Delete(migrationBuilder);

            migrationBuilder.DropTable(
                name: "SwissMunicipalities");

            migrationBuilder.DropColumn(
                name: "SwissMunicipalityCode",
                table: "Properties");
        }
    }
}
