using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// RS-7 amounts read from institutional documents on 2026-10-09. The frozen seed of the applied CO-03 / BK-03
/// migrations is unchanged; this data is applied by a later migration and kept current by the daily job when the
/// same documents can still be parsed.
/// </summary>
public static class TouristTaxOfficialExtractSeed
{
    public static readonly DateTime RetrievedAt = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    public const string MilanoUrl =
        "https://www.comune.milano.it/argomenti/tributi/imposta-di-soggiorno-informazioni";
    public const string RomaUrl =
        "https://www.comune.roma.it/web-resources/cms/documents/Nuove_tariffe_contributo_di_soggiorno_dal_01.10.2023_logo.pdf";
    public const string ComoUrl =
        "https://www.comune.como.it/export/sites/comune-di-como/.galleries/Settore-14/TARIFFE-IMPOSTA-DI-SOGGIORNO.pdf";
    public const string FirenzeUrl =
        "https://servizi.comune.fi.it/sites/www.comune.fi.it/files/deliberazione_di_giunta_completa-dg_2024_00599_00535-1.pdf";
    public const string NapoliUrl =
        "https://www.comune.napoli.it/articolo_tematico/tributi-locali/imposta-di-soggiorno/imposta-di-soggiorno-2026/";
    public const string TorinoUrl = "https://www.comune.torino.it/servizi/imposta-soggiorno-per-gestori";
    public const string VeneziaUrl =
        "https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf";
    public const string BolognaUrl =
        "https://www.comune.bologna.it/myportal/C_A944/api/content/download?id=660532c5ca8004009ad9519a";
    public const string SevesoUrl = "https://www.comune.seveso.mb.it/it/page/imposte-e-tasse-1";
    public const string CesanoMadernoUrl =
        "https://www.comune.cesano-maderno.mb.it/amministrazione/unita-organizzative/risorse-tributarie/";

    /// <summary>New rows that the frozen CO-03 / BK-03 seed never inserted (Torino, Bologna, Roma locazione breve, Venezia Gruppo 3 alta).</summary>
    public static IReadOnlyList<TouristTaxRate> NewRates() =>
    [
        Rate(
            "001272", "Torino", "PIE", null, null, null,
            TouristTaxCalculationMethod.PerPersonPerNight, 3.80m, null, null, 7, 13, null, null,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            "Locazioni turistiche e brevi: 3,80 € a persona/notte dal 1 aprile 2026 (Regolamento n. 349). Max 7 pernottamenti consecutivi. Estratto dalla pagina istituzionale del Comune di Torino il 2026-10-09.",
            TorinoUrl, "Comune di Torino"),
        Rate(
            "037006", "Bologna", "EMR", null, null, null,
            TouristTaxCalculationMethod.PercentOfNightlyPrice, 0m, 10.50m, 7.00m, null, 14, null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Locazioni brevi (portali/intermediari): 10,50% del costo dell'appartamento al netto di IVA e servizi, cap 7,00 € a persona/notte. Delibera DG/PRO/2025/283, valida 01/01-31/12/2026. Estratto dal PDF istituzionale del Comune di Bologna il 2026-10-09.",
            BolognaUrl, "Comune di Bologna"),
        Rate(
            "058091", "Roma", "LAZ", "Immobili destinati alla locazione breve", null, null,
            TouristTaxCalculationMethod.PerPersonPerNight, 6.00m, null, null, 10, 10, null, null,
            new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            "Contributo di soggiorno. Immobili destinati alla locazione breve (art. 4 D.L. 50/2017). Tabella delib. G.Ca. n. 255/2023 dal 01/10/2023. Max 10 pernottamenti. Estratto dal PDF istituzionale del Comune di Roma il 2026-10-09.",
            RomaUrl, "Comune di Roma"),
        Rate(
            "027042", "Venezia", "VEN", "Gruppo 3 (categorie catastali A/4, A/5)", "02-01", "12-31",
            TouristTaxCalculationMethod.PerPersonPerNight, 3.00m, null, null, 5, 10, 16, 1.50m,
            new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            "Locazioni turistiche, alta stagione 01/02-31/12, Gruppo 3 (A/4, A/5); tariffa ridotta 50% = 1.50. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.",
            VeneziaUrl, "Comune di Venezia"),
    ];

    public static void Apply(MigrationBuilder migrationBuilder)
    {
        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("015146", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            9.50m, MilanoUrl, "Comune di Milano",
            "Locazioni Brevi ex D.L. 50/2017: 9,50 € a persona/notte dal 1 aprile 2026 (delib. G.C. n. 144 del 12/02/2026). Estratto dalla pagina istituzionale del Comune di Milano il 2026-10-09.");
        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("013075", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            3.00m, ComoUrl, "Comune di Como",
            "Case e appartamenti per vacanze, locazioni turistiche e locazioni brevi: 3,00 € a persona/notte (delib. G.C. n. 387 del 10-11-2023, dal 01/01/2024). Max 4 pernottamenti. Estratto dal PDF istituzionale del Comune di Como il 2026-10-09.");
        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("048017", new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            6.00m, FirenzeUrl, "Comune di Firenze",
            "Locazioni di immobili ad uso turistico: 6,00 € a persona/notte (delib. G.C. n. 535 del 10/12/2024, dal 01/02/2025). Estratto dal PDF istituzionale del Comune di Firenze il 2026-10-09.");
        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("063049", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            6.00m, NapoliUrl, "Comune di Napoli",
            "Locazioni brevi: 6,00 € a persona/notte dal 01/05/2026 (delib. G.C. n. 89 del 12/03/2026). Max 14 pernottamenti consecutivi. Estratto dalla pagina istituzionale del Comune di Napoli il 2026-10-09.");

        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("058091", new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                "Case e appartamenti per vacanze, categoria 1", null),
            6.00m, RomaUrl, "Comune di Roma",
            "Contributo di soggiorno. Case e appartamenti per vacanze, categoria 1. Tabella delib. G.Ca. n. 255/2023 dal 01/10/2023. Estratto dal PDF istituzionale del Comune di Roma il 2026-10-09.");
        StampExisting(
            migrationBuilder,
            TouristTaxRateSeed.IdFor("058091", new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                "Case e appartamenti per vacanze, categoria 2", null),
            5.00m, RomaUrl, "Comune di Roma",
            "Contributo di soggiorno. Case e appartamenti per vacanze, categoria 2. Tabella delib. G.Ca. n. 255/2023 dal 01/10/2023. Estratto dal PDF istituzionale del Comune di Roma il 2026-10-09.");

        StampVeneziaExisting(migrationBuilder,
            "Gruppo 1 (categorie catastali A/1, A/8, A/9)", "02-01", 5.00m, 2.50m,
            "Locazioni turistiche, alta stagione 01/02-31/12, Gruppo 1; tariffa ridotta 50% = 2.50. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.");
        StampVeneziaExisting(migrationBuilder,
            "Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)", "02-01", 4.00m, 2.00m,
            "Locazioni turistiche, alta stagione 01/02-31/12, Gruppo 2; tariffa ridotta 50% = 2.00. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.");
        StampVeneziaExisting(migrationBuilder,
            "Gruppo 1 (categorie catastali A/1, A/8, A/9)", "01-01", 3.50m, 1.70m,
            "Locazioni turistiche, bassa stagione 01/01-31/01, Gruppo 1; tariffa ridotta 50% = 1.70. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.");
        StampVeneziaExisting(migrationBuilder,
            "Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)", "01-01", 2.80m, 1.40m,
            "Locazioni turistiche, bassa stagione 01/01-31/01, Gruppo 2; tariffa ridotta 50% = 1.40. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.");
        StampVeneziaExisting(migrationBuilder,
            "Gruppo 3 (categorie catastali A/4, A/5)", "01-01", 2.10m, 1.00m,
            "Locazioni turistiche, bassa stagione 01/01-31/01, Gruppo 3; tariffa ridotta 50% = 1.00. Estratto dal PDF istituzionale del Comune di Venezia il 2026-10-09.");

        foreach (var rate in NewRates())
        {
            migrationBuilder.InsertData(
                table: "TouristTaxRates",
                columns:
                [
                    "Id", "City", "IstatCode", "RegionCode", "AccommodationCategory", "SeasonStart", "SeasonEnd",
                    "CalculationMethod", "RatePerPersonPerNight", "PercentOfNightlyPrice", "CapPerPersonPerNight",
                    "MaxNights", "MinimumAge", "ReducedRateMaxAge", "ReducedRatePerPersonPerNight", "IsActive",
                    "EffectiveFrom", "EffectiveTo", "Notes", "SourceUrl", "SourceAuthority", "SourceRetrievedAt",
                    "VerificationLevel", "CreatedAt", "UpdatedAt",
                ],
                values:
                [
                    rate.Id, rate.City, rate.IstatCode, rate.RegionCode, rate.AccommodationCategory, rate.SeasonStart,
                    rate.SeasonEnd, rate.CalculationMethod.ToString(), rate.RatePerPersonPerNight,
                    rate.PercentOfNightlyPrice, rate.CapPerPersonPerNight, rate.MaxNights, rate.MinimumAge,
                    rate.ReducedRateMaxAge, rate.ReducedRatePerPersonPerNight, rate.IsActive, rate.EffectiveFrom,
                    rate.EffectiveTo, rate.Notes, rate.SourceUrl, rate.SourceAuthority, rate.SourceRetrievedAt,
                    rate.VerificationLevel?.ToString(), rate.CreatedAt, rate.UpdatedAt,
                ]);
        }
    }

    public static void Revert(MigrationBuilder migrationBuilder)
    {
        foreach (var rate in NewRates())
            migrationBuilder.DeleteData(table: "TouristTaxRates", keyColumn: "Id", keyValue: rate.Id);
    }

    private static void StampExisting(
        MigrationBuilder migrationBuilder,
        Guid id,
        decimal rate,
        string sourceUrl,
        string authority,
        string notes)
    {
        migrationBuilder.UpdateData(
            table: "TouristTaxRates",
            keyColumn: "Id",
            keyValue: id,
            columns: ["RatePerPersonPerNight", "Notes", "SourceUrl", "SourceAuthority", "SourceRetrievedAt", "UpdatedAt", "VerificationLevel"],
            values: [rate, notes, sourceUrl, authority, RetrievedAt, RetrievedAt, nameof(TouristTaxRateVerification.Official)]);
    }

    private static void StampVeneziaExisting(
        MigrationBuilder migrationBuilder,
        string category,
        string seasonStart,
        decimal rate,
        decimal reduced,
        string notes)
    {
        var id = TouristTaxRateSeed.IdFor(
            "027042", new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc), category, seasonStart);
        migrationBuilder.UpdateData(
            table: "TouristTaxRates",
            keyColumn: "Id",
            keyValue: id,
            columns:
            [
                "RatePerPersonPerNight", "ReducedRatePerPersonPerNight", "Notes", "SourceUrl", "SourceAuthority",
                "SourceRetrievedAt", "UpdatedAt", "VerificationLevel",
            ],
            values:
            [
                rate, reduced, notes, VeneziaUrl, "Comune di Venezia", RetrievedAt, RetrievedAt,
                nameof(TouristTaxRateVerification.Official),
            ]);
    }

    private static TouristTaxRate Rate(
        string istatCode,
        string city,
        string regionCode,
        string? category,
        string? seasonStart,
        string? seasonEnd,
        TouristTaxCalculationMethod method,
        decimal rate,
        decimal? percent,
        decimal? cap,
        int? maxNights,
        int minimumAge,
        int? reducedMaxAge,
        decimal? reducedRate,
        DateTime effectiveFrom,
        string notes,
        string sourceUrl,
        string authority)
    {
        var id = category is null && seasonStart is null
            ? TouristTaxRateSeed.IdFor(istatCode, effectiveFrom)
            : TouristTaxRateSeed.IdFor(istatCode, effectiveFrom, category, seasonStart);
        return new TouristTaxRate
        {
            Id = id,
            City = city,
            IstatCode = istatCode,
            RegionCode = regionCode,
            AccommodationCategory = category,
            SeasonStart = seasonStart,
            SeasonEnd = seasonEnd,
            CalculationMethod = method,
            RatePerPersonPerNight = rate,
            PercentOfNightlyPrice = percent,
            CapPerPersonPerNight = cap,
            MaxNights = maxNights,
            MinimumAge = minimumAge,
            ReducedRateMaxAge = reducedMaxAge,
            ReducedRatePerPersonPerNight = reducedRate,
            IsActive = true,
            EffectiveFrom = effectiveFrom,
            Notes = notes,
            SourceUrl = sourceUrl,
            SourceAuthority = authority,
            SourceRetrievedAt = RetrievedAt,
            VerificationLevel = TouristTaxRateVerification.Official,
            CreatedAt = RetrievedAt,
            UpdatedAt = RetrievedAt,
        };
    }
}
