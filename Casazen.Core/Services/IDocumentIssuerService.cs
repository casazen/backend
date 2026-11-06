using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// Who issues the fiscal documents of an organization and of each booking (FIS-02, NF-82, PO-05, E06/F02/US02/T02): the setting of the
/// organization (Octorate, CasaZen for its direct bookings, or nobody here), the rules of its regime, and the one issuer a booking ever has.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Never two issuers.</b> Where Octorate issues, even by hand in its panel, CasaZen reads and does not issue; CasaZen never issues for an OTA
/// booking, in any mode. The database refuses a second issuer row of a booking, and <see cref="LockIssuerAsync"/> answers 409 before it comes to that.</item>
/// <item><b>CasaZen issues only if it was chosen and it is able.</b> The mode <see cref="DocumentIssuerMode.CasaZenDirectOnly"/> is accepted only
/// while <c>Features:FiscalDocumentIssuance</c> is on and the profile of the issuer is complete; the mode <see cref="DocumentIssuerMode.Octorate"/>
/// only with an active Octorate connection.</item>
/// <item><b>Tenant.</b> The service works through the tenant query filter of its caller and names the org of every row it reads or writes. The web
/// layer decides who may change the settings of the org (the whole org, not some of its properties).</item>
/// </list>
/// </remarks>
public interface IDocumentIssuerService
{
    /// <summary>The setting of the organization, or the empty setting of one that has not chosen yet (no mode).</summary>
    Task<IssuerSettingsView> GetSettingsAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the answers of the wizard «Come emetti i documenti?» as a whole (what is left out is cleared). 422 <c>issuer_field_invalid</c> (with the
    /// field) for a value that is not valid; <c>document_issuance_not_available</c> for the mode CasaZen while the flag is off;
    /// <c>issuer_profile_incomplete</c> (with the <c>fields</c> that are missing) for the mode CasaZen with a profile that cannot issue;
    /// <c>octorate_not_connected</c> for the mode Octorate without an active connection. Nothing is saved when it is refused.
    /// </summary>
    Task<IssuerSettingsView> SaveSettingsAsync(
        Guid orgId,
        SaveIssuerSettingsCommand command,
        string? updatedByUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Who issues the documents of the booking and why, as a stable code (<see cref="IssuerReasons"/>): the issuer the booking is locked to, else what
    /// the setting of its organization says. It changes nothing. A booking that is not visible to the caller is a 404.
    /// </summary>
    Task<IssuerDecision> ResolveIssuerAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Locks the single issuer of the booking. Idempotent for the same kind (the first document that locked it is kept); a different kind already
    /// locked is 409 <c>document_issuer_conflict</c>. CasaZen cannot be locked as the issuer of an OTA booking (422 <c>document_booking_not_direct</c>,
    /// whatever the mode), while the flag is off (422 <c>document_issuance_not_available</c>) or when the organization chose another issuer (409
    /// <c>document_issuer_conflict</c>); Octorate can always be locked: it issued, and CasaZen reads. <paramref name="save"/> <c>false</c> only stages the
    /// row in the unit of work of the caller, who saves it with the document.
    /// </summary>
    Task<IssuerLock> LockIssuerAsync(Guid bookingId, BookingDocumentIssuerKind kind, Guid? documentId, bool save = true, CancellationToken ct = default);

    /// <summary>
    /// The rule of the regime of the organization, or 422 <c>document_rule_not_configured</c> when it has no regime, the configuration has no rule for
    /// it or the rule is not complete (<see cref="FiscalDocumentRule.IsConfigured"/>): issuance never guesses a rate.
    /// </summary>
    Task<FiscalDocumentRule> RequireRuleAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>The kind of issuer a booking resolves to (<see cref="IssuerDecision"/>).</summary>
public enum IssuerDecisionKind
{
    /// <summary>Nobody issues here.</summary>
    None = 0,

    /// <summary>Octorate issues, CasaZen reads.</summary>
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
    /// <summary>The organization chose Octorate as the issuer.</summary>
    public const string IssuerOctorate = "issuer_octorate";

    /// <summary>The organization chose CasaZen for its direct bookings and this is one (or CasaZen already issued for it).</summary>
    public const string IssuerCasazen = "issuer_casazen";

    /// <summary>Nobody issues here: the organization chose the accountant, or has not chosen yet.</summary>
    public const string IssuerNobody = "issuer_nobody";

    /// <summary>The organization chose CasaZen, but this booking comes from an OTA: CasaZen never issues for it.</summary>
    public const string BookingNotDirect = "booking_not_direct";

    /// <summary>The booking is locked to Octorate: it issued, even by hand, and CasaZen only reads.</summary>
    public const string IssuerLockedOctorate = "issuer_locked_octorate";

    /// <summary>The organization chose CasaZen, but issuance by CasaZen is switched off (<c>Features:FiscalDocumentIssuance</c>).</summary>
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

    /// <summary>422: the mode Octorate needs an active Octorate connection.</summary>
    public const string OctorateNotConnected = "octorate_not_connected";

    /// <summary>422: CasaZen does not issue for an OTA booking.</summary>
    public const string BookingNotDirect = "document_booking_not_direct";

    /// <summary>409: the booking has another issuer already, or the organization chose another one.</summary>
    public const string IssuerConflict = "document_issuer_conflict";

    /// <summary>422: the rule of the regime is not configured (nothing is issued with a rate nobody approved).</summary>
    public const string RuleNotConfigured = "document_rule_not_configured";

    /// <summary>422: a value of the setting is not valid or is too long; the <c>field</c> is in the answer.</summary>
    public const string FieldInvalid = "issuer_field_invalid";

    /// <summary>403: the setting belongs to the whole organization, not to some of its properties.</summary>
    public const string NeedsWholeOrg = "fiscal_settings_need_whole_org";

    /// <summary>The key in <c>SharedResources</c> of the message of <paramref name="code"/>.</summary>
    public static string MessageKeyOf(string code) => code switch
    {
        IssuanceNotAvailable => "DocumentIssuanceNotAvailable",
        ProfileIncomplete => "IssuerProfileIncomplete",
        OctorateNotConnected => "OctorateNotConnected",
        BookingNotDirect => "DocumentBookingNotDirect",
        IssuerConflict => "DocumentIssuerConflict",
        RuleNotConfigured => "DocumentRuleNotConfigured",
        FieldInvalid => "IssuerFieldInvalid",
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
    public const string DefaultSeries = "defaultSeries";

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
/// <param name="CountryCode">ISO 3166-1 alpha-2, <c>IT</c> by default.</param>
/// <param name="FatturaPaRegimeCode">The regime code the accountant chose, or <c>null</c> for the one of the rule.</param>
/// <param name="IssuerSdiRecipientCode">The code of the issuer's own SDI channel.</param>
/// <param name="IssuerPec">The PEC of the issuer.</param>
/// <param name="DefaultSeries">The series documents are numbered in by default.</param>
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
    string? DefaultSeries)
{
    /// <summary>The data of an issuer that has entered nothing.</summary>
    public static IssuerData Empty { get; } = new(null, null, null, null, null, null, "IT", null, null, null, null);
}

/// <summary>
/// The setting of an organization as it is now (<see cref="IDocumentIssuerService.GetSettingsAsync"/>).
/// </summary>
/// <param name="Mode">Who issues; <c>null</c> while the organization has not chosen.</param>
/// <param name="Regime">The regime of the issuer; <c>null</c> until it says it.</param>
/// <param name="Issuer">The issuer's data.</param>
/// <param name="EffectiveRegimeCode">The FatturaPA regime code that would be used: the profile's own, else the rule's; <c>null</c> when there is none.</param>
/// <param name="VatNumber">The VAT number of the organization (read from <c>Org</c>, never copied).</param>
/// <param name="FiscalCode">The fiscal code of the organization (read from <c>Org</c>).</param>
/// <param name="ConfirmedAt">When the wizard was completed.</param>
/// <param name="OctorateConnected">The organization has an active Octorate connection in this environment.</param>
/// <param name="MissingFields">What the mode CasaZen would still need (<see cref="IssuerFields"/>); empty when the profile is complete.</param>
/// <param name="Rule">The rule of the regime as configured, or <c>null</c> when there is none (no regime, or none configured for it).</param>
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
    FiscalDocumentRule? Rule);

/// <summary>
/// The answers of the wizard «Come emetti i documenti?». A save replaces the setting as a whole: a text that is left out (<c>null</c> or blank) is cleared,
/// and <see cref="Confirmed"/> is the wizard being completed (it sets <c>ConfirmedAt</c>; <c>false</c> keeps the time already there).
/// </summary>
public sealed record SaveIssuerSettingsCommand
{
    /// <summary>Who issues the documents.</summary>
    public required DocumentIssuerMode Mode { get; init; }

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

    public string? DefaultSeries { get; init; }

    public bool Confirmed { get; init; }
}
