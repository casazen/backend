using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Tourist tax rates loaded by the migration <c>AddTouristTaxRateSourceAndSeed</c> (task CO-03, defect A5-06), taken
/// from <c>Data/Seeds/tourist-tax/rates.csv</c> as researched by task RS-7 on 2026-09-23
/// (<c>.claude/context/regulations/imposta_soggiorno.md</c>, "Tariffe verificate (2026-09)").
/// </summary>
/// <remarks>
/// <para>
/// Only the rows the current <see cref="TouristTaxRate"/> model represents exactly are loaded: one fixed amount per
/// person per night for the whole comune, with an official source (<c>verified = U</c>). The values are frozen here,
/// as a migration must not change after it ran; <c>TouristTaxRateSeedTests</c> checks them against the CSV.
/// </para>
/// <para>Rows of the CSV that are NOT loaded (until task BK-03 extends the model):</para>
/// <list type="bullet">
/// <item>Roma: amount by accommodation category (short let vs CAV cat. 1/2); the short-let amount is third-party (T).</item>
/// <item>Venezia: amount by cadastral group and season, 50% reduction for ages 10-16.</item>
/// <item>Bologna: percentage of the night price with a cap, no fixed amount.</item>
/// <item>Torino: amount from a third-party source (T, "not a source"); nights capped per year, not per stay.</item>
/// <item>Seveso, Cesano Maderno: no rate found (an empty rate is not 0: the comune stays "without rate").</item>
/// </list>
/// </remarks>
public static class TouristTaxRateSeed
{
    /// <summary>Date the sources were consulted (RS-7), used as creation date of the seeded rows.</summary>
    public static readonly DateTime RetrievedAt = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>New instances of the seeded rates on every call (entities are mutable).</summary>
    public static IReadOnlyList<TouristTaxRate> BuildRates() =>
    [
        Rate(
            istatCode: "015146",
            city: "Milano",
            regionCode: "LOM",
            rate: 9.50m,
            maxNights: 14,
            minimumAge: 18,
            effectiveFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Case e appartamenti per vacanze e locazioni brevi (art. 4 D.L. 50/2017). Tariffa fissata per il solo anno 2026 (incremento olimpico D.L. 156/2025; delib. G.C. n. 1418 del 13/11/2025) e confermata a 9,50 dal 01/04/2026 (delib. n. 144 del 12/02/2026). Nel 2025 era 6,30. Tariffa dal 2027 non nota. Esenti i minori fino al 18° anno di età (art. 19 Regolamento). Max 14 pernottamenti consecutivi. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.milano.it/-/turismo.-approvate-tariffe-imposta-di-soggiorno-in-vigore-dal-1-gennaio-2026"),
        Rate(
            istatCode: "013075",
            city: "Como",
            regionCode: "LOM",
            rate: 3.00m,
            maxNights: 4,
            minimumAge: 14,
            effectiveFrom: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Case e appartamenti per vacanze, locazioni turistiche e locazioni brevi (stessa tariffa di affittacamere, foresterie lombarde e B&B). Delib. G.C. n. 387 del 10-11/11/2023, in vigore dal 01/01/2024. Max 4 pernottamenti consecutivi. Esenti i minori di 14 anni. Nessuna variazione 2026 trovata sul sito del comune. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.como.it/export/sites/comune-di-como/.galleries/Settore-14/TARIFFE-IMPOSTA-DI-SOGGIORNO.pdf"),
        Rate(
            istatCode: "048017",
            city: "Firenze",
            regionCode: "TOS",
            rate: 6.00m,
            maxNights: 7,
            minimumAge: 12,
            effectiveFrom: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche e locazioni brevi: da 5,50 a 6,00 (allineate agli alberghi 3 stelle), delib. G.C. n. 535 del 10/12/2024. Decorrenza 01/02/2025 dedotta dalla regola 'primo giorno del secondo mese successivo alla pubblicazione' e riportata da fonti terze (D/T). Max 7 pernottamenti consecutivi. Esenti i minori fino al compimento del 12° anno. Nessuna variazione 2026 trovata. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://servizi.comune.fi.it/sites/www.comune.fi.it/files/deliberazione_di_giunta_completa-dg_2024_00599_00535-1.pdf"),
        Rate(
            istatCode: "063049",
            city: "Napoli",
            regionCode: "CAM",
            rate: 6.00m,
            maxNights: 14,
            minimumAge: 14,
            effectiveFrom: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni brevi (equiparate alle strutture extralberghiere): +1,00 sui valori base di inizio 2026, delib. G.C. n. 89 del 12/03/2026, in vigore dal 01/05/2026. Tariffa 01/01-30/04/2026 non verificata (probabilmente 5,00). Max 14 pernottamenti consecutivi. Esenti i minori «entro il quattordicesimo anno di età»: formula ambigua sul quattordicenne, da chiarire. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.napoli.it/novita/napoli-aggiornate-le-tariffe-dellimposta-di-soggiorno-per-il-2026/"),
    ];

    /// <summary>Stable id of a seeded rate: same comune and start date, same id in every environment.</summary>
    public static Guid IdFor(string istatCode, DateTime effectiveFrom)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"casazen-tourist-tax-seed:{istatCode}:{effectiveFrom:yyyy-MM-dd}"));
        return new Guid(hash);
    }

    /// <summary>Inserts the rates of <see cref="BuildRates"/>. Called by the migration; parameterized by EF, no SQL text.</summary>
    public static void Insert(MigrationBuilder migrationBuilder)
    {
        foreach (var rate in BuildRates())
        {
            migrationBuilder.InsertData(
                table: "TouristTaxRates",
                columns:
                [
                    "Id", "City", "RegionCode", "RatePerPersonPerNight", "MaxNights", "MinimumAge", "IsActive",
                    "EffectiveFrom", "EffectiveTo", "Notes", "SourceUrl", "VerificationLevel", "CreatedAt", "UpdatedAt",
                ],
                values:
                [
                    rate.Id, rate.City, rate.RegionCode, rate.RatePerPersonPerNight, rate.MaxNights, rate.MinimumAge,
                    rate.IsActive, rate.EffectiveFrom, rate.EffectiveTo, rate.Notes, rate.SourceUrl,
                    rate.VerificationLevel?.ToString(), rate.CreatedAt, rate.UpdatedAt,
                ]);
        }
    }

    /// <summary>Removes the seeded rows (migration Down).</summary>
    public static void Delete(MigrationBuilder migrationBuilder)
    {
        foreach (var rate in BuildRates())
            migrationBuilder.DeleteData(table: "TouristTaxRates", keyColumn: "Id", keyValue: rate.Id);
    }

    private static TouristTaxRate Rate(
        string istatCode,
        string city,
        string regionCode,
        decimal rate,
        int maxNights,
        int minimumAge,
        DateTime effectiveFrom,
        string notes,
        string sourceUrl) => new()
        {
            Id = IdFor(istatCode, effectiveFrom),
            City = city,
            RegionCode = regionCode,
            RatePerPersonPerNight = rate,
            MaxNights = maxNights,
            MinimumAge = minimumAge,
            IsActive = true,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = null,
            Notes = notes,
            SourceUrl = sourceUrl,
            VerificationLevel = TouristTaxRateVerification.Official,
            CreatedAt = RetrievedAt,
            UpdatedAt = RetrievedAt,
        };
}
