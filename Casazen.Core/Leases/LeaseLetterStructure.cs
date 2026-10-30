namespace Casazen.Core.Leases;

/// <summary>
/// Data placeholders of a letter (<c>{{name}}</c> in the files of <c>Casazen.Web/LeaseLetters</c>, LR-OPS-1). Their values are computed from the
/// lease and from what the person typed in the window of the operation, never written in the text or in code. A value the contract does not
/// have is printed as «[da completare]».
/// </summary>
public static class LeaseLetterPlaceholders
{
    /// <summary>The landlords by name: «Mario Rossi e Anna Bianchi».</summary>
    public const string Landlords = "locatori";

    /// <summary>The landlords by name and residence, one per line.</summary>
    public const string LandlordsDetail = "locatori_dettaglio";

    /// <summary>The tenants by name.</summary>
    public const string Tenants = "conduttori";

    /// <summary>The tenants by name and residence, one per line.</summary>
    public const string TenantsDetail = "conduttori_dettaglio";

    /// <summary>Address, postal code and comune of the property.</summary>
    public const string PropertyAddress = "immobile_indirizzo";

    /// <summary>The kind of contract in words: «canone libero (4+4)», «canone concordato (3+2)», «transitorio».</summary>
    public const string ContractType = "tipo_contratto";

    /// <summary>The day the lease began.</summary>
    public const string StartDate = "data_decorrenza";

    /// <summary>The last day of the period the lease is in.</summary>
    public const string PeriodEnd = "data_scadenza";

    /// <summary>The day the withdrawal of the tenant was received.</summary>
    public const string ReceivedOn = "data_ricezione";

    /// <summary>The last day of the lease when the withdrawal takes effect.</summary>
    public const string EndsOn = "data_cessazione";

    /// <summary>The months of notice (from the rules of the area, or the one the contract gives a withdrawal).</summary>
    public const string NoticeMonths = "preavviso_mesi";

    /// <summary>The first day of the period a renewal or an extension adds.</summary>
    public const string NewPeriodStart = "data_inizio_periodo";

    /// <summary>The last day of the period a renewal or an extension adds.</summary>
    public const string NewPeriodEnd = "data_fine_periodo";

    /// <summary>The need that justified a transitory lease, as the landlord wrote it in the contract.</summary>
    public const string NeedReason = "esigenza_motivo";

    /// <summary>The monthly rent the lease has today, in euros: «650,00».</summary>
    public const string CurrentRent = "canone_attuale";

    /// <summary>The variation of the FOI index the landlord typed, in percent: «3,2».</summary>
    public const string FoiPercent = "variazione_foi";

    /// <summary>The share of the variation the contract recognizes, in percent: «75».</summary>
    public const string SharePercent = "quota_istat";

    /// <summary>What the update makes in percent of the rent: «2,4».</summary>
    public const string AppliedPercent = "aumento_percentuale";

    /// <summary>The increase of the monthly rent, in euros: «15,60».</summary>
    public const string IncreaseAmount = "aumento_importo";

    /// <summary>The monthly rent after the update, in euros: «665,60».</summary>
    public const string NewRent = "nuovo_canone";

    /// <summary>The day an update or an option takes effect from.</summary>
    public const string EffectiveOn = "data_efficacia";

    /// <summary>The day the property is handed back.</summary>
    public const string HandoverOn = "data_riconsegna";

    /// <summary>The deposit of the lease, in euros: «1.950,00».</summary>
    public const string DepositAmount = "deposito_importo";

    /// <summary>The day the deposit was paid.</summary>
    public const string DepositPaidOn = "deposito_data";

    /// <summary>The day the deposit is returned.</summary>
    public const string ReturnDate = "data_restituzione";

    /// <summary>The interest of each year the deposit was held, one line per year.</summary>
    public const string InterestDetail = "interessi_dettaglio";

    /// <summary>The interest accrued over all the years, in euros.</summary>
    public const string InterestTotal = "interessi_totale";

    /// <summary>The interest the landlord already paid the tenant, in euros.</summary>
    public const string InterestPaidAmount = "interessi_corrisposti";

    /// <summary>The interest still due: accrued minus paid, in euros.</summary>
    public const string InterestDueAmount = "interessi_dovuti";

    /// <summary>The sums the landlord keeps from the deposit and why, one line each.</summary>
    public const string DeductionsList = "trattenute_elenco";

    /// <summary>The total of the sums kept, in euros (0,00 when none).</summary>
    public const string DeductionsTotal = "trattenute_totale";

    /// <summary>What is returned to the tenant, in euros.</summary>
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

/// <summary>
/// The conditions under which a section of a letter is printed. A letter has some sections that belong to one case only (the reason of the
/// first expiry, the renunciation of the later ones…): the file holds all of them and the case picks which are printed.
/// </summary>
public static class LeaseLetterFlags
{
    /// <summary>The notice is given at the first expiry: the law asks for one of its reasons.</summary>
    public const string FirstPeriod = "first-period";

    /// <summary>The notice is given at a later expiry: no reason is asked.</summary>
    public const string LaterPeriod = "later-period";

    /// <summary>The renewal is the standard one of the type (the months the lease renews for).</summary>
    public const string RenewalStandard = "renewal-standard";

    /// <summary>The renewal is an extension to a day the parties agreed on.</summary>
    public const string RenewalAgreed = "renewal-agreed";

    /// <summary>The landlord keeps some sums from the deposit: the statement lists them.</summary>
    public const string WithDeductions = "with-deductions";

    /// <summary>The landlord already paid some of the interest during the lease: the statement says so.</summary>
    public const string InterestPaid = "interest-paid";

    /// <summary>The reason of the first expiry the landlord chose.</summary>
    public static string Reason(LeaseDisdettaReason reason) => $"reason:{reason}";

    /// <summary>The reason of the withdrawal of the tenant.</summary>
    public static string Withdrawal(TenantWithdrawalReason reason) => $"withdrawal:{reason}";
}

/// <summary>
/// A section every file of a letter must contain, with its text. <paramref name="Flag"/> (see <see cref="LeaseLetterFlags"/>) makes it
/// conditional: it is printed only when the case has that flag. <paramref name="RequiredPlaceholders"/> must all be used by the text.
/// </summary>
public sealed record LeaseLetterSectionDefinition(string Id, string? Flag = null, IReadOnlyList<string>? RequiredPlaceholders = null);

/// <summary>
/// The checklist of the sections of each letter (LR-OPS-1). Only the structure: the words are in the files, which a lawyer reviews.
/// </summary>
public static class LeaseLetterStructure
{
    /// <summary>The draft version shipped with the API for a kind, which is used until the product owner names another one (and approves it).</summary>
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

    /// <summary>The sections the file of <paramref name="kind"/> must contain, in the order they are printed.</summary>
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
