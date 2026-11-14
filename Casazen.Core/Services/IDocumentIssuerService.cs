using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who issues the fiscal documents of an organization and of each booking (FIS-02, NF-82, NF-112, E11/F01/US01/T01): CasaZen, always (decision of the
/// PO of 2026-11-14), for every active booking whatever its source; the profile of the issuer with which it issues, and the rules of its regime.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>CasaZen is the only issuer.</b> The modes Octorate and accountant are retired: a save that asks for them is 422 <c>issuer_mode_retired</c>,
/// and a document imported from Octorate never locks a booking. A booking locked to Octorate before the decision (a legacy row) keeps that lock, so a
/// booking Octorate already issued for is never issued twice.</item>
/// <item><b>Behind the flag.</b> <c>Features:FiscalDocumentIssuance</c> gates every write: while it is off nobody issues and the setting cannot be saved.</item>
/// <item><b>Tenant.</b> The service works through the tenant query filter of its caller and names the org of every row it reads or writes. The web
/// layer decides who may change the settings of the org (the whole org, not some of its properties).</item>
/// </list>
/// </remarks>
public interface IDocumentIssuerService
{
    /// <summary>The setting of the organization, or the empty setting of one that has saved nothing yet (no mode).</summary>
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
    /// Locks CasaZen as the issuer of the booking (the rows are written with CasaZen only). Idempotent (the first document that locked it is kept); a
    /// booking a legacy row locked to Octorate is 409 <c>document_issuer_conflict</c>, and while the flag is off it is 422
    /// <c>document_issuance_not_available</c>. <paramref name="save"/> <c>false</c> only stages the row in the unit of work of the caller, who saves it
    /// with the document.
    /// </summary>
    Task<IssuerLock> LockIssuerAsync(Guid bookingId, BookingDocumentIssuerKind kind, Guid? documentId, bool save = true, CancellationToken ct = default);
}

/// <summary>The kind of issuer a booking resolves to (<see cref="IssuerDecision"/>).</summary>
public enum IssuerDecisionKind
{
    /// <summary>Nobody issues: the issuance is switched off.</summary>
    None = 0,

    /// <summary>Octorate issued for the booking before the decision of 2026-11-14 (a legacy lock): CasaZen does not issue it again.</summary>
    Octorate = 1,

    /// <summary>CasaZen issues.</summary>
    CasaZen = 2,
}

/// <summary>Who would issue the documents of a booking, and the stable code of why.</summary>
/// <param name="Kind">The issuer.</param>
/// <param name="Reason">One of <see cref="IssuerReasons"/>.</param>
public sealed record IssuerDecision(IssuerDecisionKind Kind, string Reason);

/// <summary>The stable reason codes of an <see cref="IssuerDecision"/>.</summary>
public static class IssuerReasons
{
    /// <summary>CasaZen issues the documents of the booking.</summary>
    public const string IssuerCasazen = "issuer_casazen";

    /// <summary>The booking was locked to Octorate before the decision of 2026-11-14: it issued, and CasaZen does not issue the booking again.</summary>
    public const string IssuerLockedOctorate = "issuer_locked_octorate";

    /// <summary>Issuance by CasaZen is switched off (<c>Features:FiscalDocumentIssuance</c>).</summary>
    public const string IssuanceOff = "issuance_off";
}

/// <summary>The issuer a booking is locked to (<see cref="IDocumentIssuerService.LockIssuerAsync"/>).</summary>
public sealed record IssuerLock(BookingDocumentIssuerKind Issuer, DateTime LockedAt, Guid? LockedByDocumentId);

/// <summary>The stable codes of the errors of the issuer setting, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class DocumentIssuerErrorCodes
{
    /// <summary>422: issuance by CasaZen is switched off.</summary>
    public const string IssuanceNotAvailable = "document_issuance_not_available";

    /// <summary>422: the profile of the issuer is not complete; the <c>fields</c> that are missing are in the answer.</summary>
    public const string ProfileIncomplete = "issuer_profile_incomplete";

    /// <summary>422: a document can be made only for a booking (the code of a document without one).</summary>
    public const string BookingNotDirect = "document_booking_not_direct";

    /// <summary>409: the booking was locked to another issuer before the decision of 2026-11-14.</summary>
    public const string IssuerConflict = "document_issuer_conflict";

    /// <summary>422: no tax rule applies (nothing is issued with a rate nobody approved).</summary>
    public const string RuleNotConfigured = "document_rule_not_configured";

    /// <summary>422: a value of the setting is not valid or is too long; the <c>field</c> is in the answer.</summary>
    public const string FieldInvalid = "issuer_field_invalid";

    /// <summary>422: the mode Octorate or accountant, retired on 2026-11-14: CasaZen is the only issuer.</summary>
    public const string ModeRetired = "issuer_mode_retired";

    /// <summary>422: the regime belongs to another country than the one of the issuer (a Swiss regime in Italy, an Italian one in Switzerland).</summary>
    public const string RegimeCountryMismatch = "issuer_regime_country_mismatch";

    /// <summary>403: the setting belongs to the whole organization, not to some of its properties.</summary>
    public const string NeedsWholeOrg = "fiscal_settings_need_whole_org";

    /// <summary>The key in <c>SharedResources</c> of the message of <paramref name="code"/>.</summary>
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

/// <summary>The names of the fields of the issuer setting as the API says them (camel case), in <c>issuer_field_invalid</c> and in the list of what is missing.</summary>
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

    /// <summary>The fiscal code of the organization (on <c>Org</c>, edited in the tax profile).</summary>
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
    /// <summary>The data of an issuer that has entered nothing.</summary>
    public static IssuerData Empty { get; } = new(null, null, null, null, null, null, "IT", null, null, null, null);

    /// <summary>The issuer is registered for the VAT.</summary>
    public bool VatRegistered { get; init; }

    /// <summary>The Swiss VAT number (UID), <c>CHE-123.456.789</c>.</summary>
    public string? SwissVatNumber { get; init; }

    /// <summary>The IBAN the issuer is paid on.</summary>
    public string? Iban { get; init; }

    /// <summary>The QR-IBAN of a Swiss issuer.</summary>
    public string? QrIban { get; init; }

    public StayVatTreatment StayVatTreatment { get; init; } = StayVatTreatment.Standard;

    public CleaningFeeTreatment CleaningFeeTreatment { get; init; } = CleaningFeeTreatment.FollowsStay;

    /// <summary>The series pattern of the invoices to the owners; <c>null</c> for the default (<c>PM{year}</c>).</summary>
    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    /// <summary>The channels whose portal collects the tourist tax itself.</summary>
    public IReadOnlyList<BookingSource> PlatformCollectedTouristTaxChannels { get; init; } = [];

    /// <summary>The authorization of the virtual stamp duty or the number of the physical stamp (Italy).</summary>
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

/// <summary>
/// The answers of the wizard. A save replaces the setting as a whole: a text that is left out (<c>null</c> or blank) is cleared, and
/// <see cref="Confirmed"/> is the wizard being completed (it sets <c>ConfirmedAt</c> when nothing is missing; <c>false</c> keeps the time already
/// there while the profile stays complete).
/// </summary>
public sealed record SaveIssuerSettingsCommand
{
    /// <summary>Who issues the documents: <see cref="DocumentIssuerMode.CasaZen"/> (the default); the retired modes are refused.</summary>
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

    /// <summary>The VAT of the stay; <c>null</c> = <see cref="StayVatTreatment.Standard"/>.</summary>
    public StayVatTreatment? StayVatTreatment { get; init; }

    /// <summary>The VAT of the cleaning; <c>null</c> = <see cref="CleaningFeeTreatment.FollowsStay"/>.</summary>
    public CleaningFeeTreatment? CleaningFeeTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    /// <summary>The names of the <see cref="BookingSource"/> whose portal collects the tourist tax; <c>null</c> or empty = none.</summary>
    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public string? StampDutyReference { get; init; }

    public bool ConservationAgreementConfirmed { get; init; }

    public bool VirtualStampConfirmed { get; init; }

    public bool Confirmed { get; init; }
}
