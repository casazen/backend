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
    /// legal obligation, the privacy notice is only presented.
    /// </summary>
    public bool MarketingConsent { get; set; }

    /// <summary>Client IP recorded with the notice presented and the consent given (proof, art. 7.1 GDPR).</summary>
    public string ConsentIpAddress { get; set; } = string.Empty;
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
    public const string GuestsCount = "CheckInGuestsCount";
    public const string GuestTypeInvalid = "CheckInGuestTypeInvalid";
    public const string MemberWithoutHead = "CheckInMemberWithoutHead";
    public const string HeadWithoutMembers = "CheckInHeadWithoutMembers";
    public const string DateOfBirthInFuture = "CheckInDateOfBirthInFuture";
    public const string ProvinceInvalid = "CheckInProvinceInvalid";
    public const string CodeInvalid = "CheckInCodeInvalid";
    public const string CodeUnknown = "CheckInCodeUnknown";
    public const string DocumentNumberInvalid = "CheckInDocumentNumberInvalid";
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
}
