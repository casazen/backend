using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Tourist tax rates of the SEO pilot comuni of Lombardy added in 2026-10 (SEO-LOCALE), read on 2026-10-13 in the tariff
/// deliberations and regulations published on the MEF portal of the Dipartimento delle Finanze (art. 13 c. 15-quater
/// D.L. 201/2011: an act takes effect from the first day of the second month after its publication there, so
/// <c>EffectiveFrom</c> is that legal date). Applied by the migration <c>AddLombardyPilotTouristTaxRates</c>.
/// </summary>
/// <remarks>
/// <para>Only rows without ambiguity are loaded (product owner decision of 2026-10-13). Not loaded, to be confirmed with the
/// comune or a lawyer:</para>
/// <list type="bullet">
/// <item>Bellagio, Menaggio, Tremezzina: exemption of "minori entro l'undicesimo anno d'età" (under 11 or under 12?).</item>
/// <item>Varenna: the season may have been changed by C.C. 22/2024, not published on the MEF portal.</item>
/// <item>Varese: 7 nights per calendar year (the engine caps nights per stay) and a new regulation from 01/03/2026.</item>
/// <item>Luino: which of 2,50 € (case vacanze in A1-A8) and 1,00 € applies to an ordinary CAV.</item>
/// <item>Monza: minors exempt only when they stay with a guest who pays (not modelled).</item>
/// <item>Domaso: no tax until 31/12/2026 (to be instituted from 2027); Seregno: no act found.</item>
/// </list>
/// <para>
/// A rate with a season (Cernobbio, Lezzeno) leaves the nights outside it without a rate: the quote is "not available"
/// there, never 0 (an amount of 0 is not a valid rate). The rows of the villas in the A/7, A/8, A/9 cadastral categories
/// are loaded with their category: properties have none yet, so they get the base rate.
/// </para>
/// </remarks>
public static class TouristTaxLombardyPilotSeed
{
    public static readonly DateTime RetrievedAt = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    private const string MefDownload =
        "https://www1.finanze.gov.it/finanze2/dipartimentopolitichefiscali/fiscalitalocale/nuova_at/download_lib.php?";

    public const string SanSiroUrl = MefDownload + "key=0900f2308072cccd&nome=361468_DIMUNIC-09co25i162d.pdf";
    public const string GravedonaUrl = MefDownload + "key=0900f2308091012b&nome=379504_DIMUNIC-09co26m315d.pdf";
    public const string CernobbioUrl = MefDownload + "key=0900f23080b3bf71&nome=397546_DIMUNIC-09co26c520d.pdf";
    public const string LezzenoUrl = MefDownload + "key=0900f23080754dbf&nome=365634_DIMUNIC-09co25e569d.pdf";
    public const string MaccagnoUrl = MefDownload + "key=0900f23080ad0471&nome=393337_DIMUNIC-09va26m339d.pdf";
    public const string RhoUrl = MefDownload + "key=0900f23080ac9971&nome=393331_DIMUNIC-09mi26h264d.pdf";

    /// <summary>New instances of the rows on every call (entities are mutable).</summary>
    public static IReadOnlyList<TouristTaxRate> NewRates() =>
    [
        Rate(
            "013248", "San Siro", null, null, null, 1.50m, maxNights: 7, minimumAge: 14,
            new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            "Case e appartamenti per vacanze e alloggi gestiti da agenzie immobiliari: 1,50 € a persona/notte. Delib. G.C. n. 90 del 31/10/2024 (regolamento C.C. n. 28 del 29/10/2024), dal 01/01/2025; efficace dal 01/02/2025 (pubblicazione MEF 05/12/2024). Max 7 pernottamenti consecutivi. Esenti i minori di 14 anni. Imposta dovuta dai non residenti. Letto sul PDF del portale MEF il 2026-10-13.",
            SanSiroUrl, "Comune di San Siro"),
        Rate(
            "013248", "San Siro", "Altre strutture extra-alberghiere nelle categorie catastali A/7 e A/8", null, null, 5.00m,
            maxNights: 7, minimumAge: 14,
            new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            "Altre strutture extra-alberghiere in categoria catastale A/7 o A/8: 5,00 € a persona/notte. Delib. G.C. n. 90 del 31/10/2024, efficace dal 01/02/2025. Max 7 pernottamenti consecutivi. Esenti i minori di 14 anni. Letto sul PDF del portale MEF il 2026-10-13.",
            SanSiroUrl, "Comune di San Siro"),
        Rate(
            "013249", "Gravedona ed Uniti", null, null, null, 1.50m, maxNights: 10, minimumAge: 14,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Case e appartamenti per vacanze e locazioni brevi (art. 4 D.L. 50/2017): 1,50 € a persona/notte, tutto l'anno. Delib. G.C. n. 104 del 29/09/2025 (regolamento C.C. n. 25/2022 modificato dalla C.C. n. 25 del 18/11/2024), dal 01/01/2026 (pubblicazione MEF 03/10/2025). Max 10 pernottamenti consecutivi. Esenti i minori di 14 anni. Letto sul PDF del portale MEF il 2026-10-13.",
            GravedonaUrl, "Comune di Gravedona ed Uniti"),
        Rate(
            "013065", "Cernobbio", null, "04-01", "12-31", 3.00m, maxNights: 7, minimumAge: 14,
            new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            "Strutture extra-alberghiere: 3,00 € a persona/notte dal 1 aprile al 31 dicembre. Tariffe della delib. G.C. n. 181/2024 (efficace dal 01/08/2025) confermate per il 2026 dalla delib. G.C. n. 169 dell'11/11/2025; regolamento C.C. n. 53/2011 modificato dalla C.C. n. 54 del 23/11/2023. Non dovuta dall'8° pernottamento consecutivo. Esenti i minori di 14 anni. Letto sul PDF del portale MEF il 2026-10-13.",
            CernobbioUrl, "Comune di Cernobbio"),
        Rate(
            "013065", "Cernobbio", "Ville di pregio nelle categorie catastali A/1, A/8 e A/9", "04-01", "12-31", 5.00m,
            maxNights: 7, minimumAge: 14,
            new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            "Ville di pregio in categoria catastale A/1, A/8 o A/9: 5,00 € a persona/notte dal 1 aprile al 31 dicembre. Delib. G.C. n. 181/2024 confermata dalla n. 169/2025. Non dovuta dall'8° pernottamento consecutivo. Esenti i minori di 14 anni. Letto sul PDF del portale MEF il 2026-10-13.",
            CernobbioUrl, "Comune di Cernobbio"),
        Rate(
            "013126", "Lezzeno", null, "04-01", "10-31", 1.50m, maxNights: 7, minimumAge: 11,
            new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "Case e appartamenti per vacanze (imprenditoriali e non) e altre strutture: 1,50 € a persona/notte dal 1 aprile al 31 ottobre. Delib. G.C. n. 80 del 10/12/2024 (regolamento C.C. n. 22 del 28/09/2017), dal 01/01/2025; efficace dal 01/03/2025 (pubblicazione MEF 13/01/2025). Max 7 pernottamenti consecutivi. Esenti i minori «sino a 11 anni non compiuti». Letto sul PDF del portale MEF il 2026-10-13.",
            LezzenoUrl, "Comune di Lezzeno"),
        Rate(
            "012142", "Maccagno con Pino e Veddasca", null, null, null, 1.00m, maxNights: 10, minimumAge: 14,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "Case e appartamenti per vacanze (imprenditoriali e non) e altre strutture extra-alberghiere: 1,00 € a persona/notte, tutto l'anno. Delib. G.C. n. 271 del 22/12/2025 (regolamento C.C. n. 38 del 22/12/2025), dal 01/03/2026 (pubblicazione MEF 29/12/2025). Max 10 pernottamenti consecutivi, contati anche tra strutture diverse. Esenti i minori di 14 anni. Letto sul PDF del portale MEF il 2026-10-13.",
            MaccagnoUrl, "Comune di Maccagno con Pino e Veddasca"),
        Rate(
            "015182", "Rho", null, null, null, 2.00m, maxNights: 5, minimumAge: 18,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            "Case e appartamenti per vacanze e altre strutture extra-alberghiere: 2,00 € a persona/notte. Delib. G.C. n. 246 del 16/12/2025 (regolamento C.C. n. 37 del 25/05/2022), dal 01/02/2026 (pubblicazione MEF 29/12/2025). Max 5 pernottamenti consecutivi. Esenti i minori. Imposta dovuta dai non residenti. Letto sul PDF del portale MEF il 2026-10-13.",
            RhoUrl, "Comune di Rho"),
    ];

    public static void Apply(MigrationBuilder migrationBuilder)
    {
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

    private static TouristTaxRate Rate(
        string istatCode,
        string city,
        string? category,
        string? seasonStart,
        string? seasonEnd,
        decimal rate,
        int maxNights,
        int minimumAge,
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
            RegionCode = "LOM",
            AccommodationCategory = category,
            SeasonStart = seasonStart,
            SeasonEnd = seasonEnd,
            CalculationMethod = TouristTaxCalculationMethod.PerPersonPerNight,
            RatePerPersonPerNight = rate,
            MaxNights = maxNights,
            MinimumAge = minimumAge,
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
