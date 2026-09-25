using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Current Monza e Brianza territorial-agreement reference data: the Accordo locale "Quadro" signed on 15/03/2024, checked
/// against its official text by RS-8 on 2026-09-23 (<c>.claude/context/regulations/canone_concordato.md</c>, section
/// "Accordi verificati (2026-09)"). Only the values that section marks as verified (class U) are here; the
/// interpretative points (class D) are marked where they are used.
/// </summary>
/// <remarks>
/// <para>Seveso and Cesano Maderno stay <see cref="DataCompleteness.Partial"/>: the deposit date, the validity after the
/// formal expiry and the local adoption are not verified, so every range is indicative (A7-23). The other 53 comuni of
/// the agreement (55, Misinto included) are <see cref="DataCompleteness.Missing"/>.</para>
/// <para>The migrations never read this class (A7-22): <c>AddTerritorialRentAgreements</c> uses the frozen
/// <see cref="CanoneConcordatoMbSeed2024"/> and <c>ConcordatoAgreementRules</c> applies the LT-10 changes with inline
/// values. A test checks that a migrated database holds exactly these data.</para>
/// </remarks>
public static class CanoneConcordatoMbSeed
{
    public const string AgreementName = "Accordo locale Quadro — Provincia di Monza e della Brianza";
    public const string Region = "Lombardia";
    public const string SourceUrl = "https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/accordo-canone-concordato-mb.pdf";
    public const string AtaSource = "Secondary sources converging on CIPE 13/11/2003 / Delibera 87/2003 — not verified against primary text";

    /// <summary>D-elements that count for sub-fascia 3 (F1 p. 6-8, class U).</summary>
    public const string SubFascia3QualifyingTypeDElements = "D1,D2,D4,D6,D7,D9";

    public static readonly DateTime SignedDate = new(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime EffectiveDate = new(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Formal duration "18 mesi dal deposito" (F1 art. 14, class U), from <see cref="EffectiveDate"/>: 2025-11-01. The
    /// same article keeps the agreement in force until a new one is signed (<see cref="RemainsInForceUntilReplaced"/>),
    /// so this alone never blocks a range — only flags it (LT-13, A7-22, <c>agreement_expired</c>).
    /// </summary>
    public static readonly DateTime ExpiresAt = EffectiveDate.AddMonths(18);

    public const bool RemainsInForceUntilReplaced = true;

    /// <summary>RS-8, August 2026 (research-canone-concordato-mb.md § 2): no more recent agreement was found.</summary>
    public const string ExpiryNote =
        "Nessun accordo più recente reperito nelle ricerche (RS-8, agosto 2026) — verificare con le associazioni firmatarie o i Comuni prima di un uso vincolante.";

    /// <summary>RS-8 check of the tables and zones of Seveso and Cesano Maderno against the official text (F1).</summary>
    public static readonly DateTime PilotTablesVerifiedAt = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

    public const string PilotVerificationSource =
        "RS-8: research-canone-concordato-mb.md, confronto con il testo ufficiale dell'accordo (PDF)";

    /// <summary>The 55 comuni of the agreement (F1 p. 1, p. 4 art. 3).</summary>
    public static readonly string[] ProvinceComuni =
    [
        "Agrate Brianza", "Aicurzio", "Albiate", "Arcore", "Barlassina", "Bellusco",
        "Bernareggio", "Besana in Brianza", "Biassono", "Bovisio-Masciago", "Briosco",
        "Brugherio", "Burago di Molgora", "Busnago", "Camparada", "Caponago",
        "Carate Brianza", "Carnate", "Cavenago di Brianza", "Ceriano Laghetto",
        "Cesano Maderno", "Cogliate", "Concorezzo", "Cornate d'Adda", "Correzzana",
        "Desio", "Giussano", "Lazzate", "Lentate sul Seveso", "Lesmo", "Limbiate",
        "Lissone", "Macherio", "Meda", "Mezzago", "Misinto", "Monza", "Muggiò", "Nova Milanese",
        "Ornago", "Renate", "Roncello", "Ronco Briantino", "Seregno", "Seveso",
        "Sovico", "Sulbiate", "Triuggio", "Usmate Velate", "Varedo", "Vedano al Lambro",
        "Veduggio con Colzano", "Verano Brianza", "Villasanta", "Vimercate",
    ];

    public static readonly string[] PilotComuni = ["Seveso", "Cesano Maderno"];

    public static IReadOnlyList<string> MissingComuni =>
        ProvinceComuni.Except(PilotComuni, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Signatory organizations valid for Seveso and Cesano Maderno, with seat and telephone as declared in the agreement
    /// (F1 p. 3 and p. 17, class U). A.S.P.P.I. Monza signs only for other comuni. An attestation of conformity is valid
    /// only when issued jointly by one organization of each side (F1 p. 12 art. 12).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, SignatoryRole Role, string Contact)> Signatories =
    [
        ("CONIA", SignatoryRole.Inquilini, "Milano, viale Monza 137 · 02 2814151"),
        ("SICET", SignatoryRole.Inquilini, "Monza, via Dante 17/A · 039 2399259"),
        ("SUNIA", SignatoryRole.Inquilini, "Monza, via Premuda 17 · 039 2731201 · 345 6035702"),
        ("UNIAT", SignatoryRole.Inquilini, "Monza, via Ardigò 15/A"),
        ("A.P.E. Monza (aderente Confedilizia)", SignatoryRole.Proprieta, "Monza, via Mosè Bianchi 18/A · 342 5745184"),
        ("A.S.P.P.I. Comprensorio Brianza", SignatoryRole.Proprieta, "Seveso, via L. Maderna 4 · 393 6435891"),
        ("CONFABITARE", SignatoryRole.Proprieta, "Monza, via F. Magellano 21 · 380 6929090"),
        ("CONFAPPI", SignatoryRole.Proprieta, "Monza, via Ponchielli 47 · 335 5368700"),
        ("FEDERPROPRIETÀ", SignatoryRole.Proprieta, "Milano, viale Certosa 1 · 02 45478950"),
        ("U.P.P.I.", SignatoryRole.Proprieta, "Monza, via G.F. Parravicini 30 · 02 2047734"),
        ("UNIONCASA", SignatoryRole.Proprieta, "Limbiate, corso Milano 14 · 328 9050358 · 02 97135036"),
    ];

    public static Guid IdFor(string comune)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("casazen-mb-concordato:" + comune));
        return new Guid(hash);
    }

    /// <summary>Id of a signatory row: stable for a given agreement and organization name.</summary>
    public static Guid SignatoryIdFor(Guid agreementId, string name) => IdFor($"{agreementId:N}:{name}");

    /// <summary>
    /// Id of a band row, from the first square metre of its label ("Da 51 a 74 mq" → 51): the same id the 2024 seed
    /// gave the band before its bounds became half-open.
    /// </summary>
    public static Guid BandIdFor(Guid agreementId, string zone, int labelFromSqm) => IdFor($"{agreementId:N}:{zone}:{labelFromSqm}");

    public static IReadOnlyList<TerritorialRentAgreement> BuildAgreements()
    {
        return ProvinceComuni.Select(BuildAgreement).ToList();
    }

    public static IReadOnlyList<HighTensionAreaComune> BuildAtaCandidates() =>
    [
        Ata("Seveso"),
        Ata("Cesano Maderno"),
    ];

    /// <summary>
    /// Comune offices receiving the IMU communication (LT-13, A7-22): destinatari, PEC and aliquota out of the code and
    /// onto the database, an admin CRUD (<c>AdminCanoneConcordatoController</c>) away from a fix. Same uncertainty the
    /// hand-written draft used to carry (a PEC seen in two channels for Seveso, a rate derived — not published — for
    /// Cesano Maderno), now data instead of a literal in <c>ComuneImuNotificationService</c>.
    /// </summary>
    public static IReadOnlyList<ComuneImuChannel> BuildImuChannels() =>
    [
        new ComuneImuChannel
        {
            Id = IdFor("imu:Seveso"),
            Comune = "Seveso",
            Region = Region,
            RecipientOffice = "Ufficio Tributi",
            Email = "protocollo@comune.seveso.mb.it",
            Pec = "comune.seveso@pec.it",
            Instructions =
                "Canale ufficiale NON univoco — la ricerca registra anche tributi@comune.seveso.mb.it e il portale " +
                "SPID/CIE/CNS Servizi Sociali (Contratti di locazione a canone concordato). Verificare con l'Ufficio Tributi prima dell'invio.",
            DataCompleteness = DataCompleteness.Partial,
            SourceUrl = SourceUrl,
        },
        new ComuneImuChannel
        {
            Id = IdFor("imu:Cesano Maderno"),
            Comune = "Cesano Maderno",
            Region = Region,
            RecipientOffice = "U.O. Risorse Tributarie",
            Email = "risorse.tributarie@comune.cesano-maderno.mb.it",
            Pec = "risorse.finanziarie@pec.comune.cesano-maderno.mb.it",
            RatePercent = 1.04m,
            EffectiveRatePercent = 0.78m,
            RateYear = 2025,
            RateKind = ImuRateKind.Derived,
            RateNotes = "Valore derivato (aliquota 1,04% x riduzione nazionale 75% ex L. 160/2019 art. 1 c. 760), NON un'aliquota ufficiale pubblicata. Delibera 2026 non reperita alla data della ricerca.",
            DataCompleteness = DataCompleteness.Partial,
            SourceUrl = SourceUrl,
        },
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

        agreement.LastVerifiedAt = PilotTablesVerifiedAt;
        agreement.VerificationSource = PilotVerificationSource;
        agreement.Signatories = Signatories
            .Select(s => new TerritorialAgreementSignatory
            {
                Id = SignatoryIdFor(agreement.Id, s.Name),
                TerritorialRentAgreementId = agreement.Id,
                Name = s.Name,
                Role = s.Role,
                Contact = s.Contact,
            })
            .ToList();
        agreement.Bands = comune.Equals("Seveso", StringComparison.OrdinalIgnoreCase)
            ? SevesoBands(agreement.Id)
            : CesanoBands(agreement.Id);
        return agreement;
    }

    /// <summary>
    /// Rules of the agreement (F1 p. 6-9, class U), the same for every comune. Coefficients: "cumulabili", added (class D,
    /// configurable). Surface uplifts cap the square metres (40, 60), the reduction above 120 mq never goes below 120.
    /// </summary>
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
        ExpiresAt = ExpiresAt,
        RemainsInForceUntilReplaced = RemainsInForceUntilReplaced,
        ExpiryNote = ExpiryNote,
        RequiredTypeACount = 2,
        SubFascia2MinTypeBCount = 3,
        SubFascia3MinTypeCCount = 3,
        SubFascia3MinQualifyingTypeDCount = 2,
        SubFascia3QualifyingTypeDElements = SubFascia3QualifyingTypeDElements,
        SubFascia3MaxMinTypeDCount = 4,
        StoveHeatingMinTypeBCount = 4,
        CoefficientCombination = CoefficientCombination.Additive,
        FurnishedUpliftPercent = 15m,
        AirConditioningUpliftPercent = 5m,
        SmallSqmMax = 40,
        SmallSqmUpliftPercent = 20m,
        MidSqmMin = 50,
        MidSqmMax = 60,
        MidSqmUpliftPercent = 10m,
        LargeSqmMin = 120,
        LargeSqmReductionPercent = 20m,
        GarageAppurtenancePercent = 50m,
        BalconyAppurtenancePercent = 30m,
        OtherAppurtenancePercent = 25m,
        GreenAreaAppurtenancePercent = 10m,
        Duration4UpliftPercent = 3m,
        Duration5UpliftPercent = 5m,
        Duration6UpliftPercent = 6m,
    };

    // Bands of the agreement: "Fino a 50", "Da 51 a 74", "Da 75 a 99", "Oltre 100" mq. Stored as contiguous half-open
    // intervals (MinSqm, MaxSqm] so that no surface falls between two labels (A7-10): (0,50], (50,74], (74,99], (99,∞).
    // 100 mq goes to "Oltre 100". The service looks the band up on the usable square metres rounded to the whole metre
    // (0,5 up), as the cadastral surface is (DPR 138/98; class D, to be confirmed by a signatory organization).

    private static List<ConcordatoRentBand> SevesoBands(Guid agreementId) =>
    [
        Band(agreementId, "Unica", null, 0, 0, 50, 20, 57, 58, 91, 92, 109),
        Band(agreementId, "Unica", null, 51, 50, 74, 20, 52, 53, 85, 86, 100),
        Band(agreementId, "Unica", null, 75, 74, 99, 20, 45, 46, 71, 72, 86),
        Band(agreementId, "Unica", null, 100, 99, null, 20, 41, 42, 62, 63, 76),
    ];

    private const string CesanoCentraleSheets = "1,12,19,22,23,26,27,28,32,33";
    private const string CesanoSemiPerifericaSheets = "2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,20,21,24,25,29,30,31,34,35";

    private static List<ConcordatoRentBand> CesanoBands(Guid agreementId) =>
    [
        Band(agreementId, "Centrale", CesanoCentraleSheets, 0, 0, 50, 20, 65, 66, 102, 103, 120),
        Band(agreementId, "Centrale", CesanoCentraleSheets, 51, 50, 74, 20, 60, 61, 94, 95, 110),
        Band(agreementId, "Centrale", CesanoCentraleSheets, 75, 74, 99, 20, 50, 51, 80, 81, 95),
        Band(agreementId, "Centrale", CesanoCentraleSheets, 100, 99, null, 20, 45, 46, 70, 71, 85),
        Band(agreementId, "Semi periferica", CesanoSemiPerifericaSheets, 0, 0, 50, 20, 55, 56, 90, 91, 105),
        Band(agreementId, "Semi periferica", CesanoSemiPerifericaSheets, 51, 50, 74, 20, 50, 51, 85, 86, 100),
        Band(agreementId, "Semi periferica", CesanoSemiPerifericaSheets, 75, 74, 99, 20, 45, 46, 70, 71, 83),
        Band(agreementId, "Semi periferica", CesanoSemiPerifericaSheets, 100, 99, null, 20, 40, 41, 60, 61, 73),
    ];

    private static ConcordatoRentBand Band(
        Guid agreementId, string zone, string? sheets, int labelFromSqm, int aboveSqm, int? upToSqm,
        decimal s1Min, decimal s1Max, decimal s2Min, decimal s2Max, decimal s3Min, decimal s3Max) =>
        new()
        {
            Id = BandIdFor(agreementId, zone, labelFromSqm),
            TerritorialRentAgreementId = agreementId,
            ZoneName = zone,
            CadastralSheets = sheets,
            MinSqm = aboveSqm,
            MaxSqm = upToSqm,
            SubFascia1MinEurSqmYear = s1Min,
            SubFascia1MaxEurSqmYear = s1Max,
            SubFascia2MinEurSqmYear = s2Min,
            SubFascia2MaxEurSqmYear = s2Max,
            SubFascia3MinEurSqmYear = s3Min,
            SubFascia3MaxEurSqmYear = s3Max,
        };
}
