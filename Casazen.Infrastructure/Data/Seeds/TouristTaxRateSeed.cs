using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Tourist tax rates taken from <c>Data/Seeds/tourist-tax/rates.csv</c> as researched by task RS-7 on 2026-09-23
/// (<c>.claude/context/regulations/imposta_soggiorno.md</c>, "Tariffe verificate (2026-09)"), in two migrations:
/// <c>AddTouristTaxRateSourceAndSeed</c> (task CO-03, <see cref="BuildRates"/>) and <c>UnifyTouristTaxOnTouristTaxRates</c>
/// (task BK-03, <see cref="BuildCategoryAndSeasonRates"/> and the ISTAT codes of <see cref="IstatCodes"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only rows with an official amount (<c>verified = U</c>) that the model represents exactly are loaded. The values are
/// frozen here, as a migration must not change after it ran; <c>TouristTaxRateSeedTests</c> checks them against the CSV.
/// </para>
/// <para>Rows of the CSV that are NOT loaded:</para>
/// <list type="bullet">
/// <item>Roma, "alloggi per uso turistico / locazione breve": amount from third parties (T).</item>
/// <item>Venezia, Gruppo 3 high season: amount deduced (D), to confirm on the official PDF.</item>
/// <item>Bologna: 10,5% of the night price, max 7,00 €, is official, but that the percentage applies to the price per
/// person (price divided by the guests) comes from third parties only (T), and the start date is deduced (D). The
/// model supports it (<see cref="TouristTaxCalculationMethod.PercentOfNightlyPrice"/>): the admin adds it once
/// confirmed (docs/runbooks/tourist-tax-rates.md).</item>
/// <item>Torino: amount from a third-party source (T); nights capped per year, not per stay.</item>
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

    /// <summary>
    /// Rates of the migration <c>UnifyTouristTaxOnTouristTaxRates</c> (BK-03): Roma by accommodation category, Venezia by
    /// cadastral group and season, with the reduced amount "per i minori da 10 a 16 anni" (10 to 16 included) as the
    /// CSV notes publish it ("tariffa ridotta 50% = 1.70"). New instances on every call.
    /// </summary>
    public static IReadOnlyList<TouristTaxRate> BuildCategoryAndSeasonRates() =>
    [
        CategoryRate(
            istatCode: "058091",
            city: "Roma",
            regionCode: "LAZ",
            accommodationCategory: "Case e appartamenti per vacanze, categoria 1",
            seasonStart: null,
            seasonEnd: null,
            rate: 6.00m,
            maxNights: 10,
            minimumAge: 10,
            reducedRateMaxAge: null,
            reducedRate: null,
            effectiveFrom: new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Contributo di soggiorno. Case e appartamenti per vacanze, categoria 1 (delib. G.Ca. n. 255 del 17/07/2023). Max 10 pernottamenti consecutivi nell'anno solare nella stessa struttura. Esenti i minori fino al compimento del 10° anno. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.roma.it/web-resources/cms/documents/Nuove_tariffe_contributo_di_soggiorno_dal_01.10.2023_logo.pdf"),
        CategoryRate(
            istatCode: "058091",
            city: "Roma",
            regionCode: "LAZ",
            accommodationCategory: "Case e appartamenti per vacanze, categoria 2",
            seasonStart: null,
            seasonEnd: null,
            rate: 5.00m,
            maxNights: 10,
            minimumAge: 10,
            reducedRateMaxAge: null,
            reducedRate: null,
            effectiveFrom: new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Contributo di soggiorno. Case e appartamenti per vacanze, categoria 2 (delib. G.Ca. n. 255 del 17/07/2023). Max 10 pernottamenti consecutivi nell'anno solare nella stessa struttura. Esenti i minori fino al compimento del 10° anno. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.roma.it/web-resources/cms/documents/Nuove_tariffe_contributo_di_soggiorno_dal_01.10.2023_logo.pdf"),
        CategoryRate(
            istatCode: "027042",
            city: "Venezia",
            regionCode: "VEN",
            accommodationCategory: "Gruppo 1 (categorie catastali A/1, A/8, A/9)",
            seasonStart: "02-01",
            seasonEnd: "12-31",
            rate: 5.00m,
            maxNights: 5,
            minimumAge: 10,
            reducedRateMaxAge: 16,
            reducedRate: 2.50m,
            effectiveFrom: new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche, alta stagione 01/02-31/12, Gruppo 1 (categoria catastale A/1, A/8, A/9); tariffa ridotta 50% = 2.50. Zona territoriale unica per le locazioni turistiche. Max 5 pernottamenti consecutivi. Esenti i minori di 10 anni; riduzione del 50% per i minori da 10 a 16 anni. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf"),
        CategoryRate(
            istatCode: "027042",
            city: "Venezia",
            regionCode: "VEN",
            accommodationCategory: "Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)",
            seasonStart: "02-01",
            seasonEnd: "12-31",
            rate: 4.00m,
            maxNights: 5,
            minimumAge: 10,
            reducedRateMaxAge: 16,
            reducedRate: 2.00m,
            effectiveFrom: new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche, alta stagione 01/02-31/12, Gruppo 2 (categoria catastale A/2, A/3, A/6, A/7, A/11); tariffa ridotta 50% = 2.00. Zona territoriale unica per le locazioni turistiche. Max 5 pernottamenti consecutivi. Esenti i minori di 10 anni; riduzione del 50% per i minori da 10 a 16 anni. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf"),
        CategoryRate(
            istatCode: "027042",
            city: "Venezia",
            regionCode: "VEN",
            accommodationCategory: "Gruppo 1 (categorie catastali A/1, A/8, A/9)",
            seasonStart: "01-01",
            seasonEnd: "01-31",
            rate: 3.50m,
            maxNights: 5,
            minimumAge: 10,
            reducedRateMaxAge: 16,
            reducedRate: 1.70m,
            effectiveFrom: new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche, bassa stagione 01/01-31/01 (-30%), Gruppo 1 (categoria catastale A/1, A/8, A/9); tariffa ridotta 50% = 1.70. Zona territoriale unica per le locazioni turistiche. Max 5 pernottamenti consecutivi. Esenti i minori di 10 anni; riduzione del 50% per i minori da 10 a 16 anni. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf"),
        CategoryRate(
            istatCode: "027042",
            city: "Venezia",
            regionCode: "VEN",
            accommodationCategory: "Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)",
            seasonStart: "01-01",
            seasonEnd: "01-31",
            rate: 2.80m,
            maxNights: 5,
            minimumAge: 10,
            reducedRateMaxAge: 16,
            reducedRate: 1.40m,
            effectiveFrom: new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche, bassa stagione 01/01-31/01 (-30%), Gruppo 2 (categoria catastale A/2, A/3, A/6, A/7, A/11); tariffa ridotta 50% = 1.40. Zona territoriale unica per le locazioni turistiche. Max 5 pernottamenti consecutivi. Esenti i minori di 10 anni; riduzione del 50% per i minori da 10 a 16 anni. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf"),
        CategoryRate(
            istatCode: "027042",
            city: "Venezia",
            regionCode: "VEN",
            accommodationCategory: "Gruppo 3 (categorie catastali A/4, A/5)",
            seasonStart: "01-01",
            seasonEnd: "01-31",
            rate: 2.10m,
            maxNights: 5,
            minimumAge: 10,
            reducedRateMaxAge: 16,
            reducedRate: 1.00m,
            effectiveFrom: new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            notes: "Locazioni turistiche, bassa stagione 01/01-31/01 (-30%), Gruppo 3 (categoria catastale A/4, A/5); tariffa ridotta 50% = 1.00. Zona territoriale unica per le locazioni turistiche. Max 5 pernottamenti consecutivi. Esenti i minori di 10 anni; riduzione del 50% per i minori da 10 a 16 anni. Non verificato alla fonte: estratto indicizzato (lettura diretta bloccata dal proxy).",
            sourceUrl: "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf"),
    ];

    /// <summary>ISTAT code of the comuni of <see cref="BuildRates"/>, set by the migration <c>UnifyTouristTaxOnTouristTaxRates</c>.</summary>
    public static IReadOnlyDictionary<string, string> IstatCodes { get; } = new Dictionary<string, string>
    {
        ["Milano"] = "015146",
        ["Como"] = "013075",
        ["Firenze"] = "048017",
        ["Napoli"] = "063049",
    };

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

    /// <summary>
    /// Stable id of a rate of <see cref="BuildCategoryAndSeasonRates"/>: several rows share comune and start date, so
    /// the category and the season are part of the key.
    /// </summary>
    public static Guid IdFor(string istatCode, DateTime effectiveFrom, string? category, string? seasonStart)
    {
        var key = $"casazen-tourist-tax-seed:{istatCode}:{effectiveFrom:yyyy-MM-dd}:{category}:{seasonStart}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// BK-03 data of the migration <c>UnifyTouristTaxOnTouristTaxRates</c>: ISTAT code of the CO-03 rows, then the rows
    /// of <see cref="BuildCategoryAndSeasonRates"/>. Parameterized by EF, no SQL text.
    /// </summary>
    public static void InsertCategoryAndSeasonRates(MigrationBuilder migrationBuilder)
    {
        foreach (var rate in BuildRates())
        {
            migrationBuilder.UpdateData(
                table: "TouristTaxRates",
                keyColumn: "Id",
                keyValue: rate.Id,
                column: "IstatCode",
                value: IstatCodes[rate.City]);
        }

        foreach (var rate in BuildCategoryAndSeasonRates())
        {
            migrationBuilder.InsertData(
                table: "TouristTaxRates",
                columns:
                [
                    "Id", "City", "IstatCode", "RegionCode", "AccommodationCategory", "SeasonStart", "SeasonEnd",
                    "CalculationMethod", "RatePerPersonPerNight", "PercentOfNightlyPrice", "CapPerPersonPerNight",
                    "MaxNights", "MinimumAge", "ReducedRateMaxAge", "ReducedRatePerPersonPerNight", "IsActive", "EffectiveFrom",
                    "EffectiveTo", "Notes", "SourceUrl", "VerificationLevel", "CreatedAt", "UpdatedAt",
                ],
                values:
                [
                    rate.Id, rate.City, rate.IstatCode, rate.RegionCode, rate.AccommodationCategory, rate.SeasonStart,
                    rate.SeasonEnd, rate.CalculationMethod.ToString(), rate.RatePerPersonPerNight,
                    rate.PercentOfNightlyPrice, rate.CapPerPersonPerNight, rate.MaxNights, rate.MinimumAge,
                    rate.ReducedRateMaxAge, rate.ReducedRatePerPersonPerNight, rate.IsActive, rate.EffectiveFrom, rate.EffectiveTo,
                    rate.Notes, rate.SourceUrl, rate.VerificationLevel?.ToString(), rate.CreatedAt, rate.UpdatedAt,
                ]);
        }
    }

    /// <summary>Removes the rows of <see cref="BuildCategoryAndSeasonRates"/> (migration Down).</summary>
    public static void DeleteCategoryAndSeasonRates(MigrationBuilder migrationBuilder)
    {
        foreach (var rate in BuildCategoryAndSeasonRates())
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
            IstatCode = istatCode,
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

    private static TouristTaxRate CategoryRate(
        string istatCode,
        string city,
        string regionCode,
        string accommodationCategory,
        string? seasonStart,
        string? seasonEnd,
        decimal rate,
        int maxNights,
        int minimumAge,
        int? reducedRateMaxAge,
        decimal? reducedRate,
        DateTime effectiveFrom,
        string notes,
        string sourceUrl) => new()
        {
            Id = IdFor(istatCode, effectiveFrom, accommodationCategory, seasonStart),
            City = city,
            IstatCode = istatCode,
            RegionCode = regionCode,
            AccommodationCategory = accommodationCategory,
            SeasonStart = seasonStart,
            SeasonEnd = seasonEnd,
            CalculationMethod = TouristTaxCalculationMethod.PerPersonPerNight,
            RatePerPersonPerNight = rate,
            MaxNights = maxNights,
            MinimumAge = minimumAge,
            ReducedRateMaxAge = reducedRateMaxAge,
            ReducedRatePerPersonPerNight = reducedRate,
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
