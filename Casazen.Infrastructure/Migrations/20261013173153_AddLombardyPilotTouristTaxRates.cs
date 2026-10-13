using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SEO-LOCALE: tourist tax rates of the new Lombardy SEO pilots that are free of ambiguity (San Siro, Gravedona ed Uniti,
    /// Cernobbio, Lezzeno, Maccagno con Pino e Veddasca, Rho), read on 2026-10-13 in the deliberations published on the MEF
    /// portal. Data only, no schema change. The rows left out and why: <see cref="TouristTaxLombardyPilotSeed"/>.
    /// </summary>
    public partial class AddLombardyPilotTouristTaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => TouristTaxLombardyPilotSeed.Apply(migrationBuilder);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => TouristTaxLombardyPilotSeed.Revert(migrationBuilder);
    }
}
