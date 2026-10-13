using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// RS-6 / RS-7 / CO-12: provenance columns on the existing official-data tables and the audit of scheduled downloads.
    /// Seed files (not rewritten by this migration): ISTAT CSV permalink
    /// https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv retrieved 2026-10-09
    /// (aggiornato al 21/02/2026, SHA-256 57eaf945…); Alloggiati public Download.ashx tables retrieved 2026-10-09
    /// from https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Tabelle.aspx (no login).
    /// </summary>
    public partial class AddOfficialReferenceDataSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceAuthority",
                table: "TouristTaxRates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceRetrievedAt",
                table: "TouristTaxRates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Authority",
                table: "ComuneImports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetrievedAt",
                table: "ComuneImports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ComuneImports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Authority",
                table: "AlloggiatiCodeTableImports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "AlloggiatiCodeTableImports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OfficialSourceFetches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Dataset = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Authority = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficialSourceFetches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfficialSourceFetches_Dataset_RetrievedAt",
                table: "OfficialSourceFetches",
                columns: new[] { "Dataset", "RetrievedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficialSourceFetches_IstatCode",
                table: "OfficialSourceFetches",
                column: "IstatCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficialSourceFetches");

            migrationBuilder.DropColumn(
                name: "SourceAuthority",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "SourceRetrievedAt",
                table: "TouristTaxRates");

            migrationBuilder.DropColumn(
                name: "Authority",
                table: "ComuneImports");

            migrationBuilder.DropColumn(
                name: "RetrievedAt",
                table: "ComuneImports");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ComuneImports");

            migrationBuilder.DropColumn(
                name: "Authority",
                table: "AlloggiatiCodeTableImports");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "AlloggiatiCodeTableImports");
        }
    }
}
