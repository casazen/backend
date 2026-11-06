using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGuestCheckInService
{
    /// <summary>Creates a new session, returns the raw (unhashed) token.</summary>
    Task<string> CreateSessionAsync(Guid bookingId, Guid orgId);

    /// <summary>Returns the active session matching the raw token, or null if expired/invalid.</summary>
    Task<GuestCheckInSession?> GetSessionByTokenAsync(string token);

    /// <summary>
    /// What the public check-in portal may show for the raw token, or null if the token is unknown, expired or
    /// its booking is no longer eligible. A completed session exposes only its status (no booking data, no guest
    /// PII); an open one the booking context and a guest prefill whose document number is masked.
    /// </summary>
    Task<GuestCheckInPublicView?> GetPublicViewAsync(string token);

    /// <summary>
    /// The session the host sees for a booking: the completed one if the guest has submitted, otherwise the most recent
    /// (whatever its status, expired included, CO-09). Null when no link was ever issued. Use
    /// <see cref="GuestCheckInSession.EffectiveStatus"/> for its status: an open session past its expiry is expired.
    /// </summary>
    Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId);

    /// <summary>
    /// Issues a new link for the booking (host "generate" or "send link", daily send job): every open link of the booking
    /// is expired, then a session valid for <c>CheckIn:SessionLifetimeDays</c> is created with no email requested yet.
    /// </summary>
    Task<IssuedCheckInLink> IssueLinkAsync(Guid bookingId, Guid orgId);

    /// <summary>Marks <see cref="GuestCheckInSessionStatus.Scaduto"/> the open sessions whose expiry has passed (A5-27). Returns how many.</summary>
    Task<int> ExpireStaleSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Submit guest data. Returns result indicating success or duplicate.</summary>
    Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request);

    /// <summary>Expires any active session for the booking and creates a fresh one.</summary>
    Task<string> RegenerateTokenAsync(Guid bookingId, Guid orgId);

    /// <summary>Expires the session matching the raw token.</summary>
    Task ExpireTokenAsync(string token);

    /// <summary>Expires active sessions for the booking except the session matching the raw token.</summary>
    Task ExpireOtherActiveSessionsAsync(Guid bookingId, string tokenToKeep);

    /// <summary>
    /// Saves the language the guest chose on the check-in page (NF-40, one of <c>GuestLanguages.Supported</c>) on the session of the raw
    /// token: the page and the emails of the session use it. Refused for a language that is not supported, an unknown or expired token and a
    /// check-in already completed.
    /// </summary>
    Task<GuestCheckInLanguageOutcome> SetLanguageAsync(string token, string language);
}

/// <summary>Outcome of <see cref="IGuestCheckInService.SetLanguageAsync"/>.</summary>
public enum GuestCheckInLanguageOutcome
{
    /// <summary>The language is saved on the session.</summary>
    Saved = 0,

    /// <summary>Not one of the supported languages (400 <c>language_unsupported</c>).</summary>
    Unsupported = 1,

    /// <summary>The token is unknown or expired, or the booking no longer takes a check-in (404).</summary>
    NotFound = 2,

    /// <summary>The guest already completed the check-in (409 <c>checkin_already_completed</c>).</summary>
    Completed = 3,
}

/// <summary>A link just issued: the raw token exists only here (the session stores its hash).</summary>
public sealed record IssuedCheckInLink(Guid SessionId, string Token, DateTime ExpiresAt);

/// <summary>Data submitted on the guest portal: every guest of the stay (CO-12) plus the booker's consents.</summary>
public class GuestCheckInSubmitRequest
{
    /// <summary>Guests in record order: a head of family or group before its members.</summary>
    public IReadOnlyList<StayGuestInput> Guests { get; set; } = [];

    /// <summary>
    /// Optional marketing consent ticked by the guest (CO-15). There is no consent for the Alloggiati registration: it is a
    /// legal obligation, the privacy notice is only presented. Legacy single box: no channel, no marketing e-mail (it is
    /// recorded on the guest and in <see cref="GuestConsentRecord"/> only); the per-channel consent is below.
    /// </summary>
    public bool MarketingConsent { get; set; }

    /// <summary>The guest ticked the e-mail channel of the per-channel marketing consent (NF-05).</summary>
    public bool MarketingConsentEmail { get; set; }

    /// <summary>The guest ticked the WhatsApp channel of the per-channel marketing consent (NF-05).</summary>
    public bool MarketingConsentWhatsApp { get; set; }

    /// <summary>
    /// Marketing e-mail address typed in its own field, as entered (NF-05): required when a channel is ticked, ignored
    /// otherwise. Never the booking contact.
    /// </summary>
    public string? MarketingEmail { get; set; }

    /// <summary>Client IP recorded with the notice presented and the consent given (proof, art. 7.1 GDPR).</summary>
    public string ConsentIpAddress { get; set; } = string.Empty;

    /// <summary>
    /// The optional answer to «Ti serve la fattura?» (FIS-01, E06/F02/US01/T03): <c>null</c> when the guest did not answer. Never required and
    /// never a reason to refuse the check-in for what it does not say: only data that is not valid is refused (422).
    /// </summary>
    public GuestCheckInInvoice? Invoice { get; set; }
}

/// <summary>
/// The guest's answer to «Ti serve la fattura?» at the check-in (FIS-01, NF-81, E06/F02/US01/T03). <see cref="NeedInvoice"/> <c>false</c> changes
/// nothing, whatever else is sent. <c>true</c> records the «yes» and, when <see cref="Data"/> says anything about the recipient, saves it as the
/// invoice data of the booking with the source <see cref="Casazen.Core.Entities.Enums.InvoiceDataSource.CheckIn"/>: it fills what the host did not
/// write and never overwrites what the host wrote.
/// </summary>
public sealed class GuestCheckInInvoice
{
    public bool NeedInvoice { get; set; }

    /// <summary>What the guest typed about the recipient; a member that is <c>null</c> was left blank.</summary>
    public SaveInvoiceDataCommand Data { get; set; } = new();
}

/// <summary>
/// SharedResources keys of the public check-in validation messages (Italian and English texts in
/// <c>Casazen.Web/Resources/SharedResources*.resx</c>).
/// </summary>
public static class CheckInValidationKeys
{
    public const string FieldRequired = "CheckInFieldRequired";
    public const string FieldMaxLength = "CheckInFieldMaxLength";
    public const string GenderInvalid = "CheckInGenderInvalid";
    public const string DocumentTypeInvalid = "CheckInDocumentTypeInvalid";
    public const string MarketingConsentUnavailable = "CheckInMarketingConsentUnavailable";
    public const string MarketingConsentMinor = "CheckInMarketingConsentMinor";
    public const string MarketingEmailRequired = "CheckInMarketingEmailRequired";
    public const string MarketingEmailInvalid = "CheckInMarketingEmailInvalid";
    public const string GuestsCount = "CheckInGuestsCount";
    public const string GuestTypeInvalid = "CheckInGuestTypeInvalid";
    public const string MemberWithoutHead = "CheckInMemberWithoutHead";
    public const string HeadWithoutMembers = "CheckInHeadWithoutMembers";
    public const string DateOfBirthInFuture = "CheckInDateOfBirthInFuture";
    public const string ProvinceInvalid = "CheckInProvinceInvalid";
    public const string CodeInvalid = "CheckInCodeInvalid";
    public const string CodeUnknown = "CheckInCodeUnknown";
    public const string DocumentNumberInvalid = "CheckInDocumentNumberInvalid";

    /// <summary>The document issue day is after today (Europe/Rome) or before 1900 (NF-40).</summary>
    public const string DocumentIssuedOnInvalid = "CheckInDocumentIssuedOnInvalid";

    /// <summary>The document expires before the day it was issued (NF-40).</summary>
    public const string DocumentExpiresBeforeIssue = "CheckInDocumentExpiresBeforeIssue";

    /// <summary>A statistical field holds a value outside its closed list (NF-40, <c>IstatGuestFieldLists</c>).</summary>
    public const string StatisticalValueInvalid = "CheckInStatisticalValueInvalid";

    /// <summary>The tourist tax exemption is not an active reason the host may choose (NF-46).</summary>
    public const string TaxExemptionInvalid = "CheckInTaxExemptionInvalid";
}

public class GuestCheckInSubmitResult
{
    public bool Success { get; set; }
    public bool Duplicate { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? GuestId { get; set; }

    /// <summary>Field errors of the submitted data (empty when valid); keys via <see cref="StayGuestFieldError.ModelStateKey"/>.</summary>
    public IReadOnlyList<StayGuestFieldError> ValidationErrors { get; set; } = [];
}

/// <summary>Public portal view of a check-in session (see <see cref="IGuestCheckInService.GetPublicViewAsync"/>).</summary>
public sealed class GuestCheckInPublicView
{
    public required GuestCheckInSessionStatus Status { get; init; }

    /// <summary>True once the guest has submitted the data: then every other property is null.</summary>
    public bool IsCompleted { get; init; }

    public Guid? SessionId { get; init; }
    public string? PropertyName { get; init; }
    public DateTime? CheckInDate { get; init; }
    public DateTime? CheckOutDate { get; init; }

    /// <summary>Guests declared on the booking (the form starts with as many).</summary>
    public int? DeclaredGuests { get; init; }

    /// <summary>Guests on file (or the booker), to prefill the form; document numbers masked.</summary>
    public IReadOnlyList<StayGuestPrefill>? Guests { get; init; }

    /// <summary>Official tables imported, i.e. the form can offer codes for them.</summary>
    public IReadOnlyList<AlloggiatiCodeTable>? AvailableCodeTables { get; init; }

    /// <summary>Version of the privacy notice the portal shows (<c>Gdpr:PrivacyNoticeVersion</c>), or null when not configured.</summary>
    public string? PrivacyNoticeVersion { get; init; }

    /// <summary>Version of the marketing consent text; null when not configured, and then the consent is not offered.</summary>
    public string? MarketingConsentVersion { get; init; }

    /// <summary>
    /// The guest (or the host) already said that an invoice is needed (E06/F02/US01/T03). Only this and <see cref="HasInvoiceData"/> reach the public
    /// page: the link is anonymous, so the stored fiscal code, VAT number, address and PEC are never returned by it.
    /// </summary>
    public bool InvoiceRequested { get; init; }

    /// <summary>The booking already has the data of an invoice recipient (the page can say so); its content is never given.</summary>
    public bool HasInvoiceData { get; init; }

    /// <summary>
    /// The statistical fields (<c>IstatGuestFieldLists</c> names) the region of the property requires, so the page asks nothing more than
    /// the region needs (data minimization, ADR-030-R1). Empty when the comune is unknown or the region requires none; empty in this
    /// release, where the regional coverage is deferred (E03/F01/US08).
    /// </summary>
    public IReadOnlyList<string>? RequiredGuestFields { get; init; }

    /// <summary>The language the guest saved on the session (NF-40); null until the guest chooses one.</summary>
    public string? Language { get; init; }
}

/// <summary>A guest already on file, shown to prefill the portal form before completion.</summary>
public sealed class StayGuestPrefill
{
    public StayGuestType Type { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public Gender? Gender { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public bool? BornInItaly { get; init; }
    public string? BirthComuneCode { get; init; }
    public string BirthComuneName { get; init; } = string.Empty;
    public string? BirthProvince { get; init; }
    public string? BirthCountryCode { get; init; }
    public string BirthCountryName { get; init; } = string.Empty;
    public string? CitizenshipCode { get; init; }
    public string CitizenshipName { get; init; } = string.Empty;
    public GuestDocumentType? DocumentType { get; init; }
    public string? DocumentTypeCode { get; init; }

    /// <summary>Document number with all but the last characters hidden, or null when none is on file.</summary>
    public string? DocumentNumberMasked { get; init; }

    public string? DocumentIssuePlaceCode { get; init; }
    public string DocumentIssuePlaceName { get; init; } = string.Empty;

    public DateOnly? DocumentIssuedOn { get; init; }
    public DateOnly? DocumentExpiresOn { get; init; }
    public bool? ResidenceInItaly { get; init; }
    public string? ResidenceComuneCode { get; init; }
    public string? ResidenceComuneName { get; init; }
    public string? ResidenceProvince { get; init; }
    public string? ResidenceCountryCode { get; init; }
    public string? ResidenceCountryName { get; init; }
    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }

    // No tourist tax exemption: it is the host's decision and never shown on the guest page.
}
