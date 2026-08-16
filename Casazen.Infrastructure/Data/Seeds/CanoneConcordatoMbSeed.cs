using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Monza e Brianza territorial-agreement reference data (research 2026-08-16).
/// Seveso / Cesano Maderno are Partial until counsel gates close; other comuni are Missing.
/// Comune count is the official 54-comune province list — do not invent a 55th.
/// </summary>
public static class CanoneConcordatoMbSeed
{
    public const string AgreementName = "Accordo locale Quadro — Provincia di Monza e della Brianza";
    public const string Region = "Lombardia";
    public const string SourceUrl = "https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/accordo-canone-concordato-mb.pdf";
    public const string AtaSource = "Secondary sources converging on CIPE 13/11/2003 / Delibera 87/2003 — not verified against primary text";

    public static readonly DateTime SignedDate = new(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime EffectiveDate = new(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly string[] ProvinceComuni =
    [
        "Agrate Brianza", "Aicurzio", "Albiate", "Arcore", "Barlassina", "Bellusco",
        "Bernareggio", "Besana in Brianza", "Biassono", "Bovisio-Masciago", "Briosco",
        "Brugherio", "Burago di Molgora", "Busnago", "Camparada", "Caponago",
        "Carate Brianza", "Carnate", "Cavenago di Brianza", "Ceriano Laghetto",
        "Cesano Maderno", "Cogliate", "Concorezzo", "Cornate d'Adda", "Correzzana",
        "Desio", "Giussano", "Lazzate", "Lentate sul Seveso", "Lesmo", "Limbiate",
        "Lissone", "Macherio", "Meda", "Mezzago", "Monza", "Muggiò", "Nova Milanese",
        "Ornago", "Renate", "Roncello", "Ronco Briantino", "Seregno", "Seveso",
        "Sovico", "Sulbiate", "Triuggio", "Usmate Velate", "Varedo", "Vedano al Lambro",
        "Veduggio con Colzano", "Verano Brianza", "Villasanta", "Vimercate",
    ];

    public static readonly string[] PilotComuni = ["Seveso", "Cesano Maderno"];

    public static IReadOnlyList<string> MissingComuni =>
        ProvinceComuni.Except(PilotComuni, StringComparer.OrdinalIgnoreCase).ToList();

    public static Guid IdFor(string comune)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("casazen-mb-concordato:" + comune));
        return new Guid(hash);
    }

    public static IReadOnlyList<TerritorialRentAgreement> BuildAgreements()
    {
        return ProvinceComuni.Select(BuildAgreement).ToList();
    }

    public static IReadOnlyList<HighTensionAreaComune> BuildAtaCandidates() =>
    [
        Ata("Seveso"),
        Ata("Cesano Maderno"),
    ];

    private static HighTensionAreaComune Ata(string comune) => new()
    {
        Id = IdFor("ata:" + comune),
        Comune = comune,
        Region = Region,
        SourceReference = AtaSource,
        VerifiedDirectly = false,
    };

    private static TerritorialRentAgreement BuildAgreement(string comune)
    {
        var isPilot = PilotComuni.Contains(comune, StringComparer.OrdinalIgnoreCase);
        var agreement = BaseAgreement(comune, isPilot ? DataCompleteness.Partial : DataCompleteness.Missing);
        if (!isPilot)
            return agreement;

        agreement.Signatories = SharedSignatories(agreement.Id);
        agreement.Bands = comune.Equals("Seveso", StringComparison.OrdinalIgnoreCase)
            ? SevesoBands(agreement.Id)
            : CesanoBands(agreement.Id);
        return agreement;
    }

    private static TerritorialRentAgreement BaseAgreement(string comune, DataCompleteness completeness) => new()
    {
        Id = IdFor(comune),
        Comune = comune,
        Region = Region,
        AgreementName = AgreementName,
        SignedDate = SignedDate,
        EffectiveDate = EffectiveDate,
        SourceUrl = SourceUrl,
        DataCompleteness = completeness,
        RequiredTypeACount = 2,
        FurnishedUpliftPercent = 15m,
        SmallSqmMax = 40,
        SmallSqmUpliftPercent = 20m,
        MidSqmMin = 50,
        MidSqmMax = 60,
        MidSqmUpliftPercent = 10m,
        LargeSqmMin = 120,
        LargeSqmReductionPercent = 20m,
        Duration4UpliftPercent = 3m,
        Duration5UpliftPercent = 5m,
        Duration6UpliftPercent = 6m,
    };

    private static List<TerritorialAgreementSignatory> SharedSignatories(Guid agreementId) =>
    [
        Signatory(agreementId, "A.S.P.P.I. Comprensorio Brianza", SignatoryRole.Proprieta, "393 6435891"),
        Signatory(agreementId, "Confabitare Monza Brianza", SignatoryRole.Proprieta, "monzabrianza@confabitare.it"),
        Signatory(agreementId, "SUNIA-CGIL Monza e Brianza", SignatoryRole.Inquilini, "suniabrianza@cgil.lombardia.it"),
    ];

    private static TerritorialAgreementSignatory Signatory(Guid agreementId, string name, SignatoryRole role, string contact) =>
        new()
        {
            Id = IdFor($"{agreementId:N}:{name}"),
            TerritorialRentAgreementId = agreementId,
            Name = name,
            Role = role,
            Contact = contact,
        };

    private static List<ConcordatoRentBand> SevesoBands(Guid agreementId) =>
    [
        Band(agreementId, "Unica", null, 0, 50, 20, 57, 58, 91, 92, 109),
        Band(agreementId, "Unica", null, 51, 74, 20, 52, 53, 85, 86, 100),
        Band(agreementId, "Unica", null, 75, 99, 20, 45, 46, 71, 72, 86),
        Band(agreementId, "Unica", null, 100, null, 20, 41, 42, 62, 63, 76),
    ];

    private static List<ConcordatoRentBand> CesanoBands(Guid agreementId) =>
    [
        Band(agreementId, "Centrale", "1,12,19,22,23,26,27,28,32,33", 0, 50, 20, 65, 66, 102, 103, 120),
        Band(agreementId, "Centrale", "1,12,19,22,23,26,27,28,32,33", 51, 74, 20, 60, 61, 94, 95, 110),
        Band(agreementId, "Centrale", "1,12,19,22,23,26,27,28,32,33", 75, 99, 20, 50, 51, 80, 81, 95),
        Band(agreementId, "Centrale", "1,12,19,22,23,26,27,28,32,33", 100, null, 20, 45, 46, 70, 71, 85),
        Band(agreementId, "Semi periferica", "2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,20,21,24,25,29,30,31,34,35", 0, 50, 20, 55, 56, 90, 91, 105),
        Band(agreementId, "Semi periferica", "2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,20,21,24,25,29,30,31,34,35", 51, 74, 20, 50, 51, 85, 86, 100),
        Band(agreementId, "Semi periferica", "2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,20,21,24,25,29,30,31,34,35", 75, 99, 20, 45, 46, 70, 71, 83),
        Band(agreementId, "Semi periferica", "2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,20,21,24,25,29,30,31,34,35", 100, null, 20, 40, 41, 60, 61, 73),
    ];

    private static ConcordatoRentBand Band(
        Guid agreementId, string zone, string? sheets, int minSqm, int? maxSqm,
        decimal s1Min, decimal s1Max, decimal s2Min, decimal s2Max, decimal s3Min, decimal s3Max) =>
        new()
        {
            Id = IdFor($"{agreementId:N}:{zone}:{minSqm}"),
            TerritorialRentAgreementId = agreementId,
            ZoneName = zone,
            CadastralSheets = sheets,
            MinSqm = minSqm,
            MaxSqm = maxSqm,
            SubFascia1MinEurSqmYear = s1Min,
            SubFascia1MaxEurSqmYear = s1Max,
            SubFascia2MinEurSqmYear = s2Min,
            SubFascia2MaxEurSqmYear = s2Max,
            SubFascia3MinEurSqmYear = s3Min,
            SubFascia3MaxEurSqmYear = s3Max,
        };
}
