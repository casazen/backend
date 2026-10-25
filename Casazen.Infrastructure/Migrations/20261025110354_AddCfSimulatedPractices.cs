using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCfSimulatedPractices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cf_simulated_practices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PracticeId = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Comune = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Provincia = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CloseReason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ControlTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cf_simulated_practices", x => x.Id);
                },
                comment: "solo per ambienti di prova: pratiche del modulo simulato di ConcordatoFacile (ConcordatoFacile:FormMode=Simulated); vuota e inerte con FormMode=Real, la sola modalita di produzione");

            migrationBuilder.CreateIndex(
                name: "IX_cf_simulated_practices_LeaseId_SubmittedAt",
                table: "cf_simulated_practices",
                columns: new[] { "LeaseId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cf_simulated_practices_PracticeId",
                table: "cf_simulated_practices",
                column: "PracticeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cf_simulated_practices");
        }
    }
}
