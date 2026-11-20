using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSwissLocalities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwissLocalities",
                columns: table => new
                {
                    PostalCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    AddOn = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MunicipalityCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CantonCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwissLocalities", x => new { x.PostalCode, x.AddOn, x.MunicipalityCode, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwissLocalities_MunicipalityCode",
                table: "SwissLocalities",
                column: "MunicipalityCode");

            migrationBuilder.CreateIndex(
                name: "IX_SwissLocalities_NormalizedName",
                table: "SwissLocalities",
                column: "NormalizedName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwissLocalities");
        }
    }
}
