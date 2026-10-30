using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// DB-08a, data only (decision DB-D2 (a), default): the texts of the five catalog cancellation policies say what the refund formula
    /// does (full refund up to N days before the check-in, a percent up to M days before, then nothing; the tourist tax comes back in
    /// full, DB-D15 (1a)), without the internal code "(PC-02)", the "notti trattenute", the "sconto ~10 %" and the "60 giorni" that the
    /// formula never implemented; and "Non rimborsabile" refunds nothing at any time: its <c>FullRefundHours</c> was 0, which made the
    /// formula give 100% until the check-in day started (the "never refunded in full" window is 10 years of hours,
    /// <c>CancellationRefundPolicy.NeverFullRefundHours</c>). Only the five seeded rows of the global catalog are touched; the grace
    /// columns are left as they are (not implemented in this release). Down puts the previous texts and value back.
    /// </remarks>
    public partial class FixCancellationPolicyCatalog : Migration
    {
        private static readonly Guid AmpiaId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
        private static readonly Guid IntermediaId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
        private static readonly Guid ContenutaId = Guid.Parse("a1000000-0000-0000-0000-000000000003");
        private static readonly Guid AnticipataId = Guid.Parse("a1000000-0000-0000-0000-000000000004");
        private static readonly Guid NonRimborsabileId = Guid.Parse("a1000000-0000-0000-0000-000000000005");
        private static readonly DateTime FixedAt = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>24 x 365 x 10: the full-refund window of a policy that never refunds in full.</summary>
        private const int NeverFullRefundHours = 87_600;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            SetDescription(migrationBuilder, AmpiaId,
                "Rimborso totale fino a 1 giorno prima del check-in; poi nessun rimborso del prezzo del soggiorno. " +
                "L'imposta di soggiorno, se pagata, viene sempre rimborsata.");
            SetDescription(migrationBuilder, IntermediaId,
                "Rimborso totale fino a 5 giorni prima del check-in; poi 50% del prezzo del soggiorno fino al giorno prima; " +
                "poi nessun rimborso. L'imposta di soggiorno, se pagata, viene sempre rimborsata.");
            SetDescription(migrationBuilder, ContenutaId,
                "Rimborso totale fino a 14 giorni prima del check-in; poi 50% del prezzo del soggiorno fino a 7 giorni prima; " +
                "poi nessun rimborso. L'imposta di soggiorno, se pagata, viene sempre rimborsata.");
            SetDescription(migrationBuilder, AnticipataId,
                "Rimborso totale fino a 30 giorni prima del check-in; poi 50% del prezzo del soggiorno fino a 7 giorni prima; " +
                "poi nessun rimborso. L'imposta di soggiorno, se pagata, viene sempre rimborsata.");

            migrationBuilder.UpdateData(
                table: "CancellationPolicies",
                keyColumn: "Id",
                keyValue: NonRimborsabileId,
                columns: new[] { "Description", "FullRefundHours", "UpdatedAt" },
                values: new object[]
                {
                    "Nessun rimborso del prezzo del soggiorno, in nessun momento. L'imposta di soggiorno, se pagata, viene sempre rimborsata.",
                    NeverFullRefundHours,
                    FixedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            SetDescription(migrationBuilder, AmpiaId,
                "Rimborso completo fino a 24 ore prima del check-in. Dopo tale termine l'host trattiene le notti già trascorse più 1 notte aggiuntiva (PC-02).");
            SetDescription(migrationBuilder, IntermediaId,
                "Rimborso completo fino a 5 giorni prima del check-in. Dopo: l'host trattiene 1 notte più il 50% delle notti non godute (PC-02).");
            SetDescription(migrationBuilder, ContenutaId,
                "Rimborso completo fino a 14 giorni prima; 50% tra 7 e 14 giorni prima; nessun rimborso entro 7 giorni o dopo il check-in (PC-02).");
            SetDescription(migrationBuilder, AnticipataId,
                "Rimborso completo fino a 30 giorni prima; 50% tra 7 e 30 giorni prima; nessun rimborso entro 7 giorni o dopo il check-in (PC-02).");

            migrationBuilder.UpdateData(
                table: "CancellationPolicies",
                keyColumn: "Id",
                keyValue: NonRimborsabileId,
                columns: new[] { "Description", "FullRefundHours", "UpdatedAt" },
                values: new object[]
                {
                    "Nessun rimborso oltre la finestra di grazia (≥7 giorni prima, entro 24 h dalla conferma). Sconto base ~10% sul prezzo. Solo se il checkout è entro 60 giorni (PC-02).",
                    0,
                    FixedAt,
                });
        }

        private static void SetDescription(MigrationBuilder migrationBuilder, Guid id, string description) =>
            migrationBuilder.UpdateData(
                table: "CancellationPolicies",
                keyColumn: "Id",
                keyValue: id,
                columns: new[] { "Description", "UpdatedAt" },
                values: new object[] { description, FixedAt });
    }
}
