using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// RS-7: amounts read on 2026-10-09 from institutional HTML/PDF of the pilot comuni that expose a readable
    /// tariff. Frozen CO-03 / BK-03 seed rows are not rewritten here as C# seed; this migration stamps provenance
    /// and inserts Torino, Bologna, Roma locazione breve and Venezia Gruppo 3 alta. Seveso and Cesano Maderno
    /// stay without an amount (no tariff on the municipal pages).
    /// </summary>
    public partial class ApplyOfficialTouristTaxExtracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => TouristTaxOfficialExtractSeed.Apply(migrationBuilder);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => TouristTaxOfficialExtractSeed.Revert(migrationBuilder);
    }
}
