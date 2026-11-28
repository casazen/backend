using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGuestCheckInService
{
    Task<string> CreateSessionAsync(Guid bookingId, Guid orgId);

    Task<GuestCheckInSession?> GetSessionByTokenAsync(string token);

    /// <summary>
    /// What the public check-in portal may show for the raw token, or null if the token is unknown, expired or
    /// its booking is no longer eligible. A completed session exposes only its status (no booking data, no guest
    /// PII); an open one the booking context and a guest prefill whose document number is masked. Both carry the slug
    /// of the org that owns the stay (<see cref="GuestCheckInPublicView.OrgSlug"/>, public data).
    /// </summary>
    Task<GuestCheckInPublicView?> GetPublicViewAsync(string token);

    /// <summary>
    /// The session the host sees for a booking: the completed one if the guest has submitted, otherwise the most recent
    /// (whatever its status, expired included, CO-09). Null when no link was ever issued. Use
    /// <see cref="GuestCheckInSession.EffectiveStatus"/> for its status: an open session past its expiry is expired.
    /// </summary>
    Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId);

    Task<IssuedCheckInLink> IssueLinkAsync(Guid bookingId, Guid orgId);

    Task<IssuedCheckInLink> ReissueLinkAsync(Guid bookingId, Guid orgId);

    Task<int> ExpireStaleSessionsAsync(CancellationToken cancellationToken = default);

    Task<bool> RequestInvoiceAsync(string token, GuestCheckInInvoice invoice, CancellationToken ct);

    Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request);

    Task<string> RegenerateTokenAsync(Guid bookingId, Guid orgId);

    Task ExpireTokenAsync(string token);

    Task ExpireOtherActiveSessionsAsync(Guid bookingId, string tokenToKeep);

    Task<GuestCheckInLanguageOutcome> SetLanguageAsync(string token, string language);
}

public enum GuestCheckInLanguageOutcome
{
    Saved = 0,

    Unsupported = 1,

    NotFound = 2,

    Completed = 3,
}

public sealed record IssuedCheckInLink(Guid SessionId, string Token, DateTime ExpiresAt);

public class GuestCheckInSubmitRequest
{
    public IReadOnlyList<StayGuestInput> Guests { get; set; } = [];

    /// <summary>
    /// Optional marketing consent ticked by the guest (CO-15). There is no consent for the Alloggiati registration: it is a
    /// legal obligation, the privacy notice is only presented. Legacy single box: no channel, no marketing e-mail (it is
    /// recorded on the guest and in <see cref="GuestConsentRecord"/> only); the per-channel consent is below.
    /// </summary>
    public bool MarketingConsent { get; set; }

    public bool? PrivacyNoticeAcknowledged { get; set; }

    public string? HouseRulesVersion { get; set; }

    public bool MarketingConsentEmail { get; set; }

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

    public string? VehiclePlate { get; set; }
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

    public SaveInvoiceDataCommand Data { get; set; } = new();
}

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

    public const string DocumentIssuedOnInvalid = "CheckInDocumentIssuedOnInvalid";

    public const string DocumentExpiresBeforeIssue = "CheckInDocumentExpiresBeforeIssue";

    public const string StatisticalValueInvalid = "CheckInStatisticalValueInvalid";

    public const string TaxExemptionInvalid = "CheckInTaxExemptionInvalid";

    public const string VehiclePlateInvalid = "CheckInVehiclePlateInvalid";
}

public class GuestCheckInSubmitResult
{
    public bool Success { get; set; }
    public bool Duplicate { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? GuestId { get; set; }

    public IReadOnlyList<StayGuestFieldError> ValidationErrors { get; set; } = [];
}

public sealed class GuestCheckInPublicView
{
    public required GuestCheckInSessionStatus Status { get; init; }

    public bool IsCompleted { get; init; }

    /// <summary>
    /// The slug of the org that owns the stay (NF-140), filled for every usable link, a completed one included. It is public data (it is in every public address of the org), not personal
    /// data, so the rule that a completed link shows only that the check-in is done (A5-28) does not hide it. The page on an agency's own domain compares it with the org of the domain,
    /// so the check-in of another agency never opens there under the domain's name. Unlike <see cref="PrivacyNoticeOrgSlug"/> it does not depend on a published privacy notice.
    /// Null only when the org cannot be read, which the foreign key of the booking does not allow.
    /// </summary>
    public string? OrgSlug { get; init; }

    public Guid? SessionId { get; init; }
    public string? PropertyName { get; init; }
    public DateTime? CheckInDate { get; init; }
    public DateTime? CheckOutDate { get; init; }

    public int? DeclaredGuests { get; init; }

    public IReadOnlyList<StayGuestPrefill>? Guests { get; init; }

    public IReadOnlyList<AlloggiatiCodeTable>? AvailableCodeTables { get; init; }

    /// <summary>Version of the privacy notice the portal shows (<c>Gdpr:PrivacyNoticeVersion</c>), or null when not configured.</summary>
    public string? PrivacyNoticeVersion { get; init; }

    public string? PrivacyNoticeOrgSlug { get; init; }

    public string? MarketingConsentVersion { get; init; }

    /// <summary>
    /// The guest (or the host) already said that an invoice is needed (E06/F02/US01/T03). Only this and <see cref="HasInvoiceData"/> reach the public
    /// page: the link is anonymous, so the stored fiscal code, VAT number, address and PEC are never returned by it.
    /// </summary>
    public bool InvoiceRequested { get; init; }

    public bool HasInvoiceData { get; init; }

    /// <summary>
    /// The guest fields the regime of the property requires, so the page asks nothing more than it needs (data minimization, ADR-030-R1): the
    /// statistical fields (<c>IstatGuestFieldLists</c> names) the region requires, empty when the comune is unknown, the region has no verified row in
    /// the ISTAT region table (E21/F01/US01/T01) or it requires none; and, for a property in canton Ticino (NF-132), the names of
    /// <see cref="Regulatory.TicinoCheckInFields.Required"/> (the document of every adult, the home address and the state of residence).
    /// </summary>
    public IReadOnlyList<string>? RequiredGuestFields { get; init; }

    public bool AskVehiclePlate { get; init; }

    public string? Language { get; init; }
}

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
    public string? ResidenceStreet { get; init; }
    public string? ResidencePostalCode { get; init; }
    public string? ResidenceCityName { get; init; }
    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }
}
