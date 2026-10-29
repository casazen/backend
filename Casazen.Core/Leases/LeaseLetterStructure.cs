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

    public static readonly IReadOnlyList<string> All =
    [
        Landlords, LandlordsDetail, Tenants, TenantsDetail, PropertyAddress, ContractType, StartDate, PeriodEnd,
        ReceivedOn, EndsOn, NoticeMonths,
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

    /// <summary>The sections the file of <paramref name="kind"/> must contain, in the order they are printed.</summary>
    public static IReadOnlyList<LeaseLetterSectionDefinition> Sections(LeaseLetterKind kind) => kind switch
    {
        LeaseLetterKind.Disdetta => DisdettaSections,
        LeaseLetterKind.PresaAttoRecesso => PresaAttoRecessoSections,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter"),
    };
}
