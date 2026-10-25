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

    /// <summary>Identification of the APE (code and energy class), from the latest APE document of the property that has it (LT-10, LR-APE).</summary>
    public const string ApeData = "ape_estremi";

    public const string MonthlyRent = "canone_mensile";

    public const string AnnualRent = "canone_annuo";

    /// <summary>Security deposit of the lease (LT-10).</summary>
    public const string SecurityDeposit = "deposito_cauzionale";

    public const string StartDate = "data_decorrenza";

    public const string EndDate = "data_scadenza";

    /// <summary>Actual term computed from the dates (e.g. "4 anni", "18 mesi"), never a fixed "3+2" or "4+4".</summary>
    public const string Term = "durata";

    /// <summary>
    /// Transitory contract only (TR): who has the need that justifies its term, "locatore" or "conduttore" (D.M. 16/01/2017 art. 2 c. 4;
    /// art. 2 of the Allegato B).
    /// </summary>
    public const string TransitoryParty = "esigenza_parte";

    /// <summary>Transitory contract only: the need that justifies the term, as the landlord wrote it (at most 500 characters).</summary>
    public const string TransitoryNeed = "esigenza_motivo";

    /// <summary>
    /// Transitory contract only: the documentation attached to prove the need, which a contract of more than 30 days must have (art. 2 c. 4).
    /// For one of 30 days or less it is the statement that none is attached.
    /// </summary>
    public const string TransitoryDocumentation = "esigenza_documentazione";

    /// <summary>"versa" or "non versa": the alternative of art. 5 of the Allegato B, resolved by the security deposit (0 is "none").</summary>
    public const string DepositPayment = "deposito_versamento";

    /// <summary>The security deposit in monthly rents ("2", "1,5"), for the "pari a n. ... mensilità del canone" of art. 5 of the Allegato B.</summary>
    public const string DepositMonths = "deposito_mensilita";

    public static readonly IReadOnlyList<string> All =
    [
        Landlords, Tenants, PropertyAddress, PropertyComune, CadastralData, ApeData,
        MonthlyRent, AnnualRent, SecurityDeposit, StartDate, EndDate, Term,
        TransitoryParty, TransitoryNeed, TransitoryDocumentation, DepositPayment, DepositMonths,
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
/// <see cref="LeaseTemplateVariant.CedolareSecca"/> and <see cref="LeaseTemplateVariant.RegimeOrdinario"/> are free-rent leases
/// (canone libero), <see cref="LeaseTemplateVariant.CanoneConcordato"/> is the agreed-rent lease (L. 431/1998 art. 2 c. 3). Since TR the
/// transitory lease has its own variant, <see cref="LeaseTemplateVariant.Transitorio"/>: the articles of the Allegato B of the D.M. 16/01/2017,
/// which that decree makes the only model a transitory contract can use (art. 2 c. 7). Student leases are not modelled, so they have no template.
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

    // The transitory variant (TR): one section per article of the Allegato B of the D.M. 16/01/2017, which is mandatory as a whole.
    public const string TransitoryNeedSection = "esigenza";
    public const string TransitoryNonCompliance = "inadempimento";
    public const string StampAndRegistration = "bollo_registrazione";
    public const string PaymentTerms = "pagamento";
    public const string Use = "uso";
    public const string Delivery = "consegna";
    public const string ChangesAndDamages = "modifiche_danni";
    public const string TenantMeetings = "assemblee";
    public const string Installations = "impianti";
    public const string Access = "accesso";
    public const string NegotiationCommission = "commissione";
    public const string Miscellaneous = "varie";
    public const string Signatures = "firme";
    public const string ApprovedInWriting = "clausole_approvate";

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

    // ---- the transitory variant (TR) -------------------------------------------------------------------------------
    // The Allegato B has 17 articles and all of them are the model (D.M. 16/01/2017 art. 2 c. 7), so a template of the variant needs them all.
    // The preamble (parties, unit, cadastral data and energy performance) is one section, because the sentence runs through it.

    private static LeaseContractSectionDefinition Article(string id, string heading) => new(id, heading, [], []);

    private static readonly IReadOnlyList<LeaseContractSectionDefinition> TransitorioSections =
    [
        new(
            Parties, "Parti e oggetto della locazione",
            [
                LeaseContractPlaceholders.Landlords, LeaseContractPlaceholders.Tenants, LeaseContractPlaceholders.PropertyAddress,
                LeaseContractPlaceholders.CadastralData, LeaseContractPlaceholders.ApeData,
            ],
            [
                [LeaseContractPlaceholders.Landlords], [LeaseContractPlaceholders.Tenants], [LeaseContractPlaceholders.PropertyAddress],
                [LeaseContractPlaceholders.CadastralData], [LeaseContractPlaceholders.ApeData],
            ]),
        new(
            Term, "Articolo 1 (Durata)",
            [LeaseContractPlaceholders.Term, LeaseContractPlaceholders.StartDate, LeaseContractPlaceholders.EndDate],
            [[LeaseContractPlaceholders.Term], [LeaseContractPlaceholders.StartDate], [LeaseContractPlaceholders.EndDate]]),
        new(
            TransitoryNeedSection, "Articolo 2 (Esigenza del locatore/conduttore)",
            [LeaseContractPlaceholders.TransitoryParty, LeaseContractPlaceholders.TransitoryNeed, LeaseContractPlaceholders.TransitoryDocumentation],
            [
                [LeaseContractPlaceholders.TransitoryParty], [LeaseContractPlaceholders.TransitoryNeed],
                [LeaseContractPlaceholders.TransitoryDocumentation],
            ]),
        Article(TransitoryNonCompliance, "Articolo 3 (Inadempimento delle modalità di stipula)"),
        new(Rent, "Articolo 4 (Canone)", [LeaseContractPlaceholders.MonthlyRent], [[LeaseContractPlaceholders.MonthlyRent]]),
        new(
            Deposit, "Articolo 5 (Deposito cauzionale e altre forme di garanzia)",
            [LeaseContractPlaceholders.DepositPayment, LeaseContractPlaceholders.SecurityDeposit, LeaseContractPlaceholders.DepositMonths],
            [
                [LeaseContractPlaceholders.DepositPayment], [LeaseContractPlaceholders.SecurityDeposit],
                [LeaseContractPlaceholders.DepositMonths],
            ]),
        Article(AncillaryCharges, "Articolo 6 (Oneri accessori)"),
        Article(StampAndRegistration, "Articolo 7 (Spese di bollo e registrazione)"),
        Article(PaymentTerms, "Articolo 8 (Pagamento)"),
        Article(Use, "Articolo 9 (Uso)"),
        Article(TenantWithdrawal, "Articolo 10 (Recesso del conduttore)"),
        Article(Delivery, "Articolo 11 (Consegna)"),
        Article(ChangesAndDamages, "Articolo 12 (Modifiche e danni)"),
        Article(TenantMeetings, "Articolo 13 (Assemblee)"),
        Article(Installations, "Articolo 14 (Impianti)"),
        Article(Access, "Articolo 15 (Accesso)"),
        Article(NegotiationCommission, "Articolo 16 (Commissione di negoziazione paritetica e conciliazione stragiudiziale)"),
        Article(Miscellaneous, "Articolo 17 (Varie)"),
        Article(Signatures, "Firme"),
        Article(ApprovedInWriting, "Clausole approvate specificamente"),
    ];

    /// <summary>The sections a template of <paramref name="variant"/> must contain, in the suggested order.</summary>
    public static IReadOnlyList<LeaseContractSectionDefinition> RequiredSections(LeaseTemplateVariant variant) => variant switch
    {
        LeaseTemplateVariant.CanoneConcordato => CanoneConcordatoSections,
        LeaseTemplateVariant.CedolareSecca => CedolareSeccaSections,
        LeaseTemplateVariant.RegimeOrdinario => FreeRentSections,
        LeaseTemplateVariant.Transitorio => TransitorioSections,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown template variant."),
    };

    /// <summary>The sections a template of <paramref name="regime"/> (a canone libero or concordato) must contain, in the suggested order.</summary>
    public static IReadOnlyList<LeaseContractSectionDefinition> RequiredSections(FiscalRegime regime) =>
        RequiredSections(LeaseTemplateVariants.Of(regime));
}
