using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// Data placeholders of a lease contract template (<c>{{name}}</c> in the clause texts). Their values are always
/// computed from the lease (parties, property, dates, rent), never written in the template or in code (LT-03, A7-03).
/// </summary>
public static class LeaseContractPlaceholders
{
    /// <summary>Every landlord: name, surname and fiscal code.</summary>
    public const string Landlords = "locatori";

    /// <summary>Every tenant: name, surname and fiscal code.</summary>
    public const string Tenants = "conduttori";

    /// <summary>Address, postal code and comune of the property.</summary>
    public const string PropertyAddress = "immobile_indirizzo";

    public const string PropertyComune = "immobile_comune";

    /// <summary>Cadastral identification of the unit, from the property (sheet, parcel, subaltern, category, income; LT-10).</summary>
    public const string CadastralData = "dati_catastali";

    /// <summary>Identification of the APE (code and energy class), from the latest APE document of the property (LT-10).</summary>
    public const string ApeData = "ape_estremi";

    public const string MonthlyRent = "canone_mensile";

    public const string AnnualRent = "canone_annuo";

    /// <summary>Security deposit of the lease (LT-10).</summary>
    public const string SecurityDeposit = "deposito_cauzionale";

    public const string StartDate = "data_decorrenza";

    public const string EndDate = "data_scadenza";

    /// <summary>Actual term computed from the dates (e.g. "4 anni", "18 mesi"), never a fixed "3+2" or "4+4".</summary>
    public const string Term = "durata";

    public static readonly IReadOnlyList<string> All =
    [
        Landlords, Tenants, PropertyAddress, PropertyComune, CadastralData, ApeData,
        MonthlyRent, AnnualRent, SecurityDeposit, StartDate, EndDate, Term,
    ];

    public static bool IsKnown(string name) => All.Contains(name, StringComparer.Ordinal);
}

/// <summary>
/// A section every template of a regime must contain, with a non-empty clause text provided by the product owner.
/// </summary>
/// <param name="Id">Stable id used in the template file (<c>## id</c>).</param>
/// <param name="DefaultHeading">Heading shown when the file gives none (a label, not a clause).</param>
/// <param name="DataPlaceholders">Lease data that belongs to the section (shown in the preview when the text is missing).</param>
/// <param name="RequiredPlaceholders">Each group lists alternatives: the text must use at least one placeholder of every group.</param>
public sealed record LeaseContractSectionDefinition(
    string Id,
    string DefaultHeading,
    IReadOnlyList<string> DataPlaceholders,
    IReadOnlyList<IReadOnlyList<string>> RequiredPlaceholders);

/// <summary>
/// Checklist of the sections a lease contract template must contain for each fiscal regime (LT-03, A7-03).
/// Only the structure: the clause texts come from the files of the product owner and are never written here.
/// Sources of each section: <c>docs/runbooks/lease-contract-templates.md</c>.
/// </summary>
/// <remarks>
/// <see cref="FiscalRegime.CedolareSecca"/> and <see cref="FiscalRegime.RegimeOrdinario"/> are free-rent leases
/// (canone libero), <see cref="FiscalRegime.CanoneConcordato"/> is the agreed-rent lease (L. 431/1998 art. 2 c. 3).
/// Transitional and student leases are not modelled yet, so they have no template.
/// </remarks>
public static class LeaseContractTemplateStructure
{
    public const string Parties = "parti";
    public const string Property = "immobile";
    public const string Term = "durata";
    public const string RenewalAndNotice = "rinnovo_disdetta";
    public const string TenantWithdrawal = "recesso";
    public const string Rent = "canone";
    public const string TerritorialAgreement = "accordo_territoriale";
    public const string ConformityAttestation = "attestazione_conformita";
    public const string RentUpdate = "aggiornamento_canone";
    public const string CedolareOption = "opzione_cedolare";
    public const string Deposit = "deposito";
    public const string AncillaryCharges = "oneri_accessori";
    public const string Ape = "ape";

    private static readonly LeaseContractSectionDefinition PartiesSection = new(
        Parties, "Parti",
        [LeaseContractPlaceholders.Landlords, LeaseContractPlaceholders.Tenants],
        [[LeaseContractPlaceholders.Landlords], [LeaseContractPlaceholders.Tenants]]);

    private static readonly LeaseContractSectionDefinition PropertySection = new(
        Property, "Oggetto della locazione",
        [LeaseContractPlaceholders.PropertyAddress, LeaseContractPlaceholders.PropertyComune, LeaseContractPlaceholders.CadastralData],
        [[LeaseContractPlaceholders.PropertyAddress]]);

    private static readonly LeaseContractSectionDefinition TermSection = new(
        Term, "Durata",
        [LeaseContractPlaceholders.Term, LeaseContractPlaceholders.StartDate, LeaseContractPlaceholders.EndDate],
        [[LeaseContractPlaceholders.Term], [LeaseContractPlaceholders.StartDate], [LeaseContractPlaceholders.EndDate]]);

    private static readonly LeaseContractSectionDefinition RenewalSection = new(
        RenewalAndNotice, "Rinnovo e disdetta", [], []);

    private static readonly LeaseContractSectionDefinition WithdrawalSection = new(
        TenantWithdrawal, "Recesso del conduttore", [], []);

    private static readonly LeaseContractSectionDefinition RentSection = new(
        Rent, "Canone",
        [LeaseContractPlaceholders.MonthlyRent, LeaseContractPlaceholders.AnnualRent],
        [[LeaseContractPlaceholders.MonthlyRent, LeaseContractPlaceholders.AnnualRent]]);

    private static readonly LeaseContractSectionDefinition TerritorialAgreementSection = new(
        TerritorialAgreement, "Accordo territoriale", [LeaseContractPlaceholders.PropertyComune], []);

    private static readonly LeaseContractSectionDefinition AttestationSection = new(
        ConformityAttestation, "Attestazione di conformità", [], []);

    private static readonly LeaseContractSectionDefinition RentUpdateSection = new(
        RentUpdate, "Aggiornamento del canone", [], []);

    private static readonly LeaseContractSectionDefinition CedolareSection = new(
        CedolareOption, "Opzione per la cedolare secca", [], []);

    private static readonly LeaseContractSectionDefinition DepositSection = new(
        Deposit, "Deposito cauzionale", [LeaseContractPlaceholders.SecurityDeposit], []);

    private static readonly LeaseContractSectionDefinition AncillarySection = new(
        AncillaryCharges, "Oneri accessori", [], []);

    private static readonly LeaseContractSectionDefinition ApeSection = new(
        Ape, "Attestato di prestazione energetica (APE)", [LeaseContractPlaceholders.ApeData], []);

    private static readonly IReadOnlyList<LeaseContractSectionDefinition> FreeRentSections =
    [
        PartiesSection, PropertySection, TermSection, RenewalSection, WithdrawalSection, RentSection,
        RentUpdateSection, DepositSection, AncillarySection, ApeSection,
    ];

    private static readonly IReadOnlyList<LeaseContractSectionDefinition> CedolareSeccaSections =
    [
        PartiesSection, PropertySection, TermSection, RenewalSection, WithdrawalSection, RentSection,
        RentUpdateSection, CedolareSection, DepositSection, AncillarySection, ApeSection,
    ];

    private static readonly IReadOnlyList<LeaseContractSectionDefinition> CanoneConcordatoSections =
    [
        PartiesSection, PropertySection, TermSection, RenewalSection, WithdrawalSection, RentSection,
        TerritorialAgreementSection, AttestationSection, RentUpdateSection, DepositSection, AncillarySection,
        ApeSection,
    ];

    /// <summary>The sections a template of <paramref name="regime"/> must contain, in the suggested order.</summary>
    public static IReadOnlyList<LeaseContractSectionDefinition> RequiredSections(FiscalRegime regime) => regime switch
    {
        FiscalRegime.CanoneConcordato => CanoneConcordatoSections,
        FiscalRegime.CedolareSecca => CedolareSeccaSections,
        FiscalRegime.RegimeOrdinario => FreeRentSections,
        _ => throw new ArgumentOutOfRangeException(nameof(regime), regime, "Unknown fiscal regime."),
    };
}
