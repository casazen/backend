using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who issues the fiscal documents of an organization and of each booking (FIS-02, NF-82, NF-112, E11/F01/US01/T01): CasaZen, always (decision of the
/// PO of 2026-11-14), for every active booking whatever its source except one that already has a document issued elsewhere; the profile of the issuer with
/// which it issues, and the rules of its regime.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>CasaZen is the only issuer.</b> The modes Octorate and accountant are retired: a save that asks for them is 422 <c>issuer_mode_retired</c>,
/// and a document imported from Octorate never locks a booking. A booking locked to Octorate before the decision (a legacy row) keeps that lock, and a
/// booking with a document imported from Octorate is refused by the document service (409 <c>document_already_issued</c>): a booking Octorate already
/// issued for is never issued twice. Every other active booking is CasaZen's.</item>
/// <item><b>Behind the flag.</b> <c>Features:FiscalDocumentIssuance</c> gates every write: while it is off nobody issues and the setting cannot be saved.</item>
/// <item><b>Tenant.</b> The service works through the tenant query filter of its caller and names the org of every row it reads or writes. The web
/// layer decides who may change the settings of the org (the whole org, not some of its properties).</item>
/// </list>
/// </remarks>
public interface IDocumentIssuerService
{
    Task<IssuerSettingsView> GetSettingsAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the answers of the wizard as a whole (what is left out is cleared). 422 <c>issuer_mode_retired</c> for the mode Octorate or accountant;
    /// <c>issuer_regime_country_mismatch</c> for a regime of the other country; <c>issuer_field_invalid</c> (with the field) for a value that is not valid;
    /// <c>document_issuance_not_available</c> while the flag is off; <c>issuer_profile_incomplete</c> (with the <c>fields</c> that are missing) when the
    /// wizard is confirmed with a profile that cannot issue yet. An incomplete profile that is not confirmed is saved as a draft. Nothing is saved when
    /// it is refused.
    /// </summary>
    Task<IssuerSettingsView> SaveSettingsAsync(
        Guid orgId,
        SaveIssuerSettingsCommand command,
        string? updatedByUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Who issues the documents of the booking and why, as a stable code (<see cref="IssuerReasons"/>): CasaZen for every booking while the issuance is
    /// on; nobody while it is off; Octorate only for a booking a legacy row locked to it. It changes nothing. A booking that is not visible to the caller
    /// is a 404.
    /// </summary>
    Task<IssuerDecision> ResolveIssuerAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Records CasaZen as the issuer of the document <paramref name="documentId"/> of the booking (the rows are written with CasaZen only): one row for each
    /// document (E17/F01/US01/T02), idempotent for the same document (a legacy row made before any document is completed by the first one); a booking a legacy
    /// row locked to Octorate is 409 <c>document_issuer_conflict</c>, and while the flag is off it is 422 <c>document_issuance_not_available</c>.
    /// <paramref name="save"/> <c>false</c> only stages the row in the unit of work of the caller, who saves it with the document.
    /// </summary>
    Task<IssuerLock> LockIssuerAsync(Guid bookingId, BookingDocumentIssuerKind kind, Guid? documentId, bool save = true, CancellationToken ct = default);
}

public enum IssuerDecisionKind
{
    None = 0,

    /// <summary>Octorate issued for the booking before the decision of 2026-11-14 (a legacy lock): CasaZen does not issue it again.</summary>
    Octorate = 1,

    CasaZen = 2,
}

public sealed record IssuerDecision(IssuerDecisionKind Kind, string Reason);

public static class IssuerReasons
{
    public const string IssuerCasazen = "issuer_casazen";

    /// <summary>The booking was locked to Octorate before the decision of 2026-11-14: it issued, and CasaZen does not issue the booking again.</summary>
    public const string IssuerLockedOctorate = "issuer_locked_octorate";

    public const string IssuanceOff = "issuance_off";
}

public sealed record IssuerLock(BookingDocumentIssuerKind Issuer, DateTime LockedAt, Guid? LockedByDocumentId);

public static class DocumentIssuerErrorCodes
{
    public const string IssuanceNotAvailable = "document_issuance_not_available";

    public const string ProfileIncomplete = "issuer_profile_incomplete";

    public const string BookingNotDirect = "document_booking_not_direct";

    public const string IssuerConflict = "document_issuer_conflict";

    public const string RuleNotConfigured = "document_rule_not_configured";

    public const string FieldInvalid = "issuer_field_invalid";

    public const string ModeRetired = "issuer_mode_retired";

    public const string RegimeCountryMismatch = "issuer_regime_country_mismatch";

    public const string NeedsWholeOrg = "fiscal_settings_need_whole_org";

    public static string MessageKeyOf(string code) => code switch
    {
        IssuanceNotAvailable => "DocumentIssuanceNotAvailable",
        ProfileIncomplete => "IssuerProfileIncomplete",
        BookingNotDirect => "DocumentBookingNotDirect",
        IssuerConflict => "DocumentIssuerConflict",
        RuleNotConfigured => "DocumentRuleNotConfigured",
        FieldInvalid => "IssuerFieldInvalid",
        ModeRetired => "IssuerModeRetired",
        RegimeCountryMismatch => "IssuerRegimeCountryMismatch",
        NeedsWholeOrg => "FiscalSettingsNeedWholeOrg",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a code of the issuer setting."),
    };
}

public static class IssuerFields
{
    public const string Mode = "mode";
    public const string Regime = "regime";
    public const string LegalName = "legalName";
    public const string AddressStreet = "addressStreet";
    public const string AddressCivic = "addressCivic";
    public const string PostalCode = "postalCode";
    public const string City = "city";
    public const string Province = "province";
    public const string CountryCode = "countryCode";
    public const string FatturaPaRegimeCode = "fatturaPaRegimeCode";
    public const string IssuerSdiRecipientCode = "issuerSdiRecipientCode";
    public const string IssuerPec = "issuerPec";
    public const string VatRegistered = "vatRegistered";
    public const string SwissVatNumber = "swissVatNumber";
    public const string Iban = "iban";
    public const string QrIban = "qrIban";
    public const string StayVatTreatment = "stayVatTreatment";
    public const string CleaningFeeTreatment = "cleaningFeeTreatment";
    public const string GuestDocumentSeries = "guestDocumentSeries";
    public const string OwnerInvoiceSeries = "ownerInvoiceSeries";
    public const string PlatformCollectedTouristTaxChannels = "platformCollectedTouristTaxChannels";
    public const string StampDutyReference = "stampDutyReference";

    /// <summary>The VAT number of the organization (on <c>Org</c>, edited in the tax profile).</summary>
    public const string VatNumber = "vatNumber";

    public const string FiscalCode = "fiscalCode";
}

/// <summary>The issuer's data as the wizard shows and saves it.</summary>
/// <param name="LegalName">The legal name of the issuer.</param>
/// <param name="AddressStreet">The street.</param>
/// <param name="AddressCivic">The house number.</param>
/// <param name="PostalCode">The postal code.</param>
/// <param name="City">The city or comune.</param>
/// <param name="Province">The two letters of the province.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, <c>IT</c> by default; <c>CH</c> for a Swiss issuer.</param>
/// <param name="FatturaPaRegimeCode">The regime code the accountant chose, or <c>null</c> for the one of the rule.</param>
/// <param name="IssuerSdiRecipientCode">The code of the issuer's own SDI channel.</param>
/// <param name="IssuerPec">The PEC of the issuer.</param>
/// <param name="GuestDocumentSeries">The series pattern of the documents to the guests; <c>null</c> for the default (the year).</param>
public sealed record IssuerData(
    string? LegalName,
    string? AddressStreet,
    string? AddressCivic,
    string? PostalCode,
    string? City,
    string? Province,
    string CountryCode,
    string? FatturaPaRegimeCode,
    string? IssuerSdiRecipientCode,
    string? IssuerPec,
    string? GuestDocumentSeries)
{
    public static IssuerData Empty { get; } = new(null, null, null, null, null, null, "IT", null, null, null, null);

    public bool VatRegistered { get; init; }

    public string? SwissVatNumber { get; init; }

    public string? Iban { get; init; }

    public string? QrIban { get; init; }

    public StayVatTreatment StayVatTreatment { get; init; } = StayVatTreatment.Standard;

    public CleaningFeeTreatment CleaningFeeTreatment { get; init; } = CleaningFeeTreatment.FollowsStay;

    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    public IReadOnlyList<BookingSource> PlatformCollectedTouristTaxChannels { get; init; } = [];

    /// <summary>
    /// The issuer confirmed those channels, even to say that none collects it (E11/F01/US01/T04). In the setting of an organization: they were
    /// confirmed at least once; in the data of a save: the save carried the list, so it is written (an empty one as «none»), otherwise the saved choice stays.
    /// </summary>
    public bool PlatformCollectedTouristTaxChannelsConfirmed { get; init; }

    public string? StampDutyReference { get; init; }

    public bool ConservationAgreementConfirmed { get; init; }

    public bool VirtualStampConfirmed { get; init; }
}

/// <summary>
/// The setting of an organization as it is now (<see cref="IDocumentIssuerService.GetSettingsAsync"/>).
/// </summary>
/// <param name="Mode">Who issues: <see cref="DocumentIssuerMode.CasaZen"/>; <c>null</c> while the organization has saved nothing.</param>
/// <param name="Regime">The regime of the issuer; <c>null</c> until it says it.</param>
/// <param name="Issuer">The issuer's data.</param>
/// <param name="EffectiveRegimeCode">The FatturaPA regime code that would be used: the profile's own, else the rule's; <c>null</c> when there is none.</param>
/// <param name="VatNumber">The VAT number of the organization (read from <c>Org</c>, never copied).</param>
/// <param name="FiscalCode">The fiscal code of the organization (read from <c>Org</c>).</param>
/// <param name="ConfirmedAt">When the wizard was completed.</param>
/// <param name="OctorateConnected">The organization has an active Octorate connection in this environment.</param>
/// <param name="MissingFields">What CasaZen still needs to issue (<see cref="IssuerFields"/>); empty when the profile is complete.</param>
/// <param name="Rule">
/// The tax rule in force today for the country, the regime and the treatments of the profile (<see cref="IFiscalTaxRuleResolver"/>), or <c>null</c> when
/// none applies (no regime, or the catalog does not cover it).
/// </param>
public sealed record IssuerSettingsView(
    DocumentIssuerMode? Mode,
    DocumentRegime? Regime,
    IssuerData Issuer,
    string? EffectiveRegimeCode,
    string? VatNumber,
    string? FiscalCode,
    DateTime? ConfirmedAt,
    bool OctorateConnected,
    IReadOnlyList<string> MissingFields,
    FiscalTaxRule? Rule);

public sealed record SaveIssuerSettingsCommand
{
    public DocumentIssuerMode Mode { get; init; } = DocumentIssuerMode.CasaZen;

    public DocumentRegime? Regime { get; init; }

    public string? LegalName { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public string? IssuerSdiRecipientCode { get; init; }

    public string? IssuerPec { get; init; }

    /// <summary>Registered for the VAT; <c>null</c> = what the regime says (without a regime: not registered).</summary>
    public bool? VatRegistered { get; init; }

    public string? SwissVatNumber { get; init; }

    public string? Iban { get; init; }

    public string? QrIban { get; init; }

    public StayVatTreatment? StayVatTreatment { get; init; }

    public CleaningFeeTreatment? CleaningFeeTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public string? StampDutyReference { get; init; }

    public bool ConservationAgreementConfirmed { get; init; }

    public bool VirtualStampConfirmed { get; init; }

    public bool Confirmed { get; init; }
}
