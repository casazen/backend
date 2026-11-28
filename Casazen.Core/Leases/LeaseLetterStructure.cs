namespace Casazen.Core.Leases;

/// <summary>
/// Data placeholders of a letter (<c>{{name}}</c> in the files of <c>Casazen.Web/LeaseLetters</c>, LR-OPS-1). Their values are computed from the
/// lease and from what the person typed in the window of the operation, never written in the text or in code. A value the contract does not
/// have is printed as «[da completare]».
/// </summary>
public static class LeaseLetterPlaceholders
{
    public const string Landlords = "locatori";

    public const string LandlordsDetail = "locatori_dettaglio";

    public const string Tenants = "conduttori";

    public const string TenantsDetail = "conduttori_dettaglio";

    public const string PropertyAddress = "immobile_indirizzo";

    public const string ContractType = "tipo_contratto";

    public const string StartDate = "data_decorrenza";

    public const string PeriodEnd = "data_scadenza";

    public const string ReceivedOn = "data_ricezione";

    public const string EndsOn = "data_cessazione";

    public const string NoticeMonths = "preavviso_mesi";

    public const string NewPeriodStart = "data_inizio_periodo";

    public const string NewPeriodEnd = "data_fine_periodo";

    public const string NeedReason = "esigenza_motivo";

    public const string CurrentRent = "canone_attuale";

    public const string FoiPercent = "variazione_foi";

    public const string SharePercent = "quota_istat";

    public const string AppliedPercent = "aumento_percentuale";

    public const string IncreaseAmount = "aumento_importo";

    public const string NewRent = "nuovo_canone";

    public const string EffectiveOn = "data_efficacia";

    public const string HandoverOn = "data_riconsegna";

    public const string DepositAmount = "deposito_importo";

    public const string DepositPaidOn = "deposito_data";

    public const string ReturnDate = "data_restituzione";

    public const string InterestDetail = "interessi_dettaglio";

    public const string InterestTotal = "interessi_totale";

    public const string InterestPaidAmount = "interessi_corrisposti";

    public const string InterestDueAmount = "interessi_dovuti";

    public const string DeductionsList = "trattenute_elenco";

    public const string DeductionsTotal = "trattenute_totale";

    public const string ReturnedAmount = "importo_restituito";

    public static readonly IReadOnlyList<string> All =
    [
        Landlords, LandlordsDetail, Tenants, TenantsDetail, PropertyAddress, ContractType, StartDate, PeriodEnd,
        ReceivedOn, EndsOn, NoticeMonths, NewPeriodStart, NewPeriodEnd, NeedReason,
        CurrentRent, FoiPercent, SharePercent, AppliedPercent, IncreaseAmount, NewRent, EffectiveOn, HandoverOn,
        DepositAmount, DepositPaidOn, ReturnDate, InterestDetail, InterestTotal, InterestPaidAmount, InterestDueAmount,
        DeductionsList, DeductionsTotal, ReturnedAmount,
    ];

    public static bool IsKnown(string name) => All.Contains(name, StringComparer.Ordinal);
}

public static class LeaseLetterFlags
{
    public const string FirstPeriod = "first-period";

    public const string LaterPeriod = "later-period";

    public const string RenewalStandard = "renewal-standard";

    public const string RenewalAgreed = "renewal-agreed";

    public const string WithDeductions = "with-deductions";

    public const string InterestPaid = "interest-paid";

    public static string Reason(LeaseDisdettaReason reason) => $"reason:{reason}";

    public static string Withdrawal(TenantWithdrawalReason reason) => $"withdrawal:{reason}";
}

public sealed record LeaseLetterSectionDefinition(string Id, string? Flag = null, IReadOnlyList<string>? RequiredPlaceholders = null);

public static class LeaseLetterStructure
{
    public static string DraftVersion(LeaseLetterKind kind) => kind switch
    {
        LeaseLetterKind.Disdetta => "2026-11-bozza-v1",
        LeaseLetterKind.PresaAttoRecesso => "2026-11-bozza-v1",
        LeaseLetterKind.Rinnovo => "2026-11-bozza-v1",
        LeaseLetterKind.ConfermaEsigenza => "2026-11-bozza-v1",
        LeaseLetterKind.AggiornamentoIstat => "2026-11-bozza-v1",
        LeaseLetterKind.OpzioneCedolare => "2026-11-bozza-v1",
        LeaseLetterKind.Riconsegna => "2026-11-bozza-v1",
        LeaseLetterKind.RestituzioneCauzione => "2026-11-bozza-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter"),
    };

    private static LeaseLetterSectionDefinition Reason(string id, LeaseDisdettaReason reason) => new(id, LeaseLetterFlags.Reason(reason));

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> DisdettaSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("comunicazione", RequiredPlaceholders: [LeaseLetterPlaceholders.PeriodEnd]),
        Reason("motivo_uso_proprio", LeaseDisdettaReason.OwnUse),
        Reason("motivo_ente", LeaseDisdettaReason.LegalEntityUse),
        Reason("motivo_altro_immobile", LeaseDisdettaReason.TenantOtherHome),
        Reason("motivo_edificio_danneggiato", LeaseDisdettaReason.DamagedBuilding),
        Reason("motivo_lavori", LeaseDisdettaReason.Renovation),
        Reason("motivo_non_occupa", LeaseDisdettaReason.TenantAbsent),
        Reason("motivo_vendita", LeaseDisdettaReason.Sale),
        new("rinuncia", LeaseLetterFlags.LaterPeriod),
        new("riconsegna"),
        new("invio"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> PresaAttoRecessoSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("ricezione_gravi_motivi", LeaseLetterFlags.Withdrawal(TenantWithdrawalReason.GraveMotivi), [LeaseLetterPlaceholders.ReceivedOn]),
        new("ricezione_facolta", LeaseLetterFlags.Withdrawal(TenantWithdrawalReason.ContractualFaculty), [LeaseLetterPlaceholders.ReceivedOn]),
        new("effetto", RequiredPlaceholders: [LeaseLetterPlaceholders.EndsOn, LeaseLetterPlaceholders.NoticeMonths]),
        new("riconsegna"),
        new("invio"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> RinnovoSections =
    [
        new("parti", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail, LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("rinnovo", LeaseLetterFlags.RenewalStandard, [LeaseLetterPlaceholders.NewPeriodStart, LeaseLetterPlaceholders.NewPeriodEnd]),
        new("proroga", LeaseLetterFlags.RenewalAgreed, [LeaseLetterPlaceholders.NewPeriodStart, LeaseLetterPlaceholders.NewPeriodEnd]),
        new("condizioni"),
        new("registrazione"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> ConfermaEsigenzaSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("conferma", RequiredPlaceholders: [LeaseLetterPlaceholders.NeedReason, LeaseLetterPlaceholders.PeriodEnd]),
        new("effetti"),
        new("invio"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> AggiornamentoIstatSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("richiesta", RequiredPlaceholders: [LeaseLetterPlaceholders.CurrentRent, LeaseLetterPlaceholders.EffectiveOn]),
        new(
            "calcolo",
            RequiredPlaceholders:
            [
                LeaseLetterPlaceholders.FoiPercent, LeaseLetterPlaceholders.SharePercent, LeaseLetterPlaceholders.AppliedPercent,
                LeaseLetterPlaceholders.IncreaseAmount, LeaseLetterPlaceholders.NewRent,
            ]),
        new("invio"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> OpzioneCedolareSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("opzione", RequiredPlaceholders: [LeaseLetterPlaceholders.EffectiveOn]),
        new("rinuncia"),
        new("invio"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> RiconsegnaSections =
    [
        new("parti", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail, LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new("riconsegna", RequiredPlaceholders: [LeaseLetterPlaceholders.HandoverOn, LeaseLetterPlaceholders.PeriodEnd]),
        new("stato"),
        new("contatori"),
        new("chiavi"),
        new("annotazioni"),
        new("chiusura"),
    ];

    private static readonly IReadOnlyList<LeaseLetterSectionDefinition> RestituzioneCauzioneSections =
    [
        new("mittente", RequiredPlaceholders: [LeaseLetterPlaceholders.LandlordsDetail]),
        new("destinatario", RequiredPlaceholders: [LeaseLetterPlaceholders.TenantsDetail]),
        new("oggetto", RequiredPlaceholders: [LeaseLetterPlaceholders.PropertyAddress]),
        new(
            "deposito",
            RequiredPlaceholders: [LeaseLetterPlaceholders.DepositAmount, LeaseLetterPlaceholders.DepositPaidOn, LeaseLetterPlaceholders.ReturnDate]),
        new("interessi", RequiredPlaceholders: [LeaseLetterPlaceholders.InterestDetail, LeaseLetterPlaceholders.InterestTotal]),
        new(
            "interessi_corrisposti",
            LeaseLetterFlags.InterestPaid,
            [LeaseLetterPlaceholders.InterestPaidAmount, LeaseLetterPlaceholders.InterestDueAmount]),
        new("trattenute", LeaseLetterFlags.WithDeductions, [LeaseLetterPlaceholders.DeductionsList, LeaseLetterPlaceholders.DeductionsTotal]),
        new(
            "restituzione",
            RequiredPlaceholders:
            [
                LeaseLetterPlaceholders.DepositAmount, LeaseLetterPlaceholders.InterestDueAmount, LeaseLetterPlaceholders.DeductionsTotal,
                LeaseLetterPlaceholders.ReturnedAmount, LeaseLetterPlaceholders.ReturnDate,
            ]),
        new("chiusura"),
    ];

    public static IReadOnlyList<LeaseLetterSectionDefinition> Sections(LeaseLetterKind kind) => kind switch
    {
        LeaseLetterKind.Disdetta => DisdettaSections,
        LeaseLetterKind.PresaAttoRecesso => PresaAttoRecessoSections,
        LeaseLetterKind.Rinnovo => RinnovoSections,
        LeaseLetterKind.ConfermaEsigenza => ConfermaEsigenzaSections,
        LeaseLetterKind.AggiornamentoIstat => AggiornamentoIstatSections,
        LeaseLetterKind.OpzioneCedolare => OpzioneCedolareSections,
        LeaseLetterKind.Riconsegna => RiconsegnaSections,
        LeaseLetterKind.RestituzioneCauzione => RestituzioneCauzioneSections,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter"),
    };
}
