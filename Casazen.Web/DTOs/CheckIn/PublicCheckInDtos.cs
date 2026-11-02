using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// Public check-in portal context. Once the guest has submitted (<see cref="Completed"/>) only
/// <see cref="Completed"/> and <see cref="Status"/> are returned: no booking data and no guest PII (A5-28).
/// </summary>
public class PublicCheckInContextResponse
{
    public bool Completed { get; set; }
    public string Status { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SessionId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertyName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CheckInDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CheckOutDate { get; set; }

    /// <summary>Guests declared on the booking: the form starts with as many.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeclaredGuests { get; set; }

    /// <summary>Guests on file (at least the booker) in record order, document numbers masked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PublicCheckInGuestPrefill>? Guests { get; set; }

    /// <summary>Official Alloggiati tables imported: the form offers their codes (e.g. <c>Comuni</c>, <c>Stati</c>, <c>Documenti</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AlloggiatiCodeTable>? AvailableCodeTables { get; set; }

    /// <summary>Version of the privacy notice (art. 13 GDPR) the portal shows; absent when not configured (CO-15).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>
    /// Version of the optional marketing consent text; absent when the consent is not offered (CO-15). Absent: do not show the
    /// consent step, the server rejects every marketing consent field set to true (<see cref="PublicCheckInSubmitRequest"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MarketingConsentVersion { get; set; }
}

/// <summary>A guest on file. The document number is never returned in full.</summary>
public class PublicCheckInGuestPrefill
{
    public StayGuestType Type { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public Gender? Gender { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool? BornInItaly { get; set; }
    public string? BirthComuneCode { get; set; }
    public string BirthComuneName { get; set; } = string.Empty;
    public string? BirthProvince { get; set; }
    public string? BirthCountryCode { get; set; }
    public string BirthCountryName { get; set; } = string.Empty;
    public string? CitizenshipCode { get; set; }
    public string CitizenshipName { get; set; } = string.Empty;
    public GuestDocumentType? DocumentType { get; set; }
    public string? DocumentTypeCode { get; set; }

    /// <summary>Document number on file with only its last characters visible (e.g. <c>*****567</c>); never the full number.</summary>
    public string? DocumentNumberMasked { get; set; }

    public string? DocumentIssuePlaceCode { get; set; }
    public string DocumentIssuePlaceName { get; set; } = string.Empty;

    public static PublicCheckInGuestPrefill From(StayGuestPrefill prefill) => new()
    {
        Type = prefill.Type,
        FirstName = prefill.FirstName,
        LastName = prefill.LastName,
        Gender = prefill.Gender,
        DateOfBirth = prefill.DateOfBirth,
        BornInItaly = prefill.BornInItaly,
        BirthComuneCode = prefill.BirthComuneCode,
        BirthComuneName = prefill.BirthComuneName,
        BirthProvince = prefill.BirthProvince,
        BirthCountryCode = prefill.BirthCountryCode,
        BirthCountryName = prefill.BirthCountryName,
        CitizenshipCode = prefill.CitizenshipCode,
        CitizenshipName = prefill.CitizenshipName,
        DocumentType = prefill.DocumentType,
        DocumentTypeCode = prefill.DocumentTypeCode,
        DocumentNumberMasked = prefill.DocumentNumberMasked,
        DocumentIssuePlaceCode = prefill.DocumentIssuePlaceCode,
        DocumentIssuePlaceName = prefill.DocumentIssuePlaceName,
    };
}

/// <summary>
/// Data submitted from the public portal: every guest of the stay (CO-12) and the consents. Every <c>[Required]</c>
/// property must also be sent by the frontend (<c>PublicCheckInSubmitRequest</c> and <c>StayGuestSubmit</c> in
/// <c>src/types/public-checkin.types.ts</c>): both sides have a contract test. Validation messages are SharedResources
/// keys (<see cref="CheckInValidationKeys"/>); the conditional fields (birth place, document) are checked by
/// <see cref="IStayGuestService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Marketing consent contract (NF-05, Q1-05).</b> Optional and never a condition of the check-in: a guest who skips the
/// consent screen sends none of <see cref="MarketingConsentEmail"/>, <see cref="MarketingConsentWhatsApp"/>,
/// <see cref="MarketingEmail"/> (or sends them false/null) and the check-in completes. The screen comes after the mandatory
/// steps, the boxes start unticked, and it is offered only when the context carries a <c>marketingConsentVersion</c>.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Who consents.</b> The booker: the first element of <see cref="Guests"/>. If that guest is a minor on the day of the
/// submission, a ticked channel is rejected (<see cref="CheckInValidationKeys.MarketingConsentMinor"/>); the screen should
/// not be shown to a minor at all (AC-NF05-3).
/// </description></item>
/// <item><description>
/// <b>Channels.</b> Send <c>true</c> only for the box the guest ticked: each channel is recorded as given, or not given,
/// on its own, and a channel is never implied by another one.
/// </description></item>
/// <item><description>
/// <b>Marketing e-mail.</b> <see cref="MarketingEmail"/> is required as soon as one channel is ticked (the double opt-in
/// confirmation is by e-mail, for every channel, and nothing is sent on any channel before the guest confirms it). When no
/// channel is ticked it is ignored: not validated and not stored.
/// </description></item>
/// <item><description>
/// <b>Text version.</b> The version recorded is the <c>marketingConsentVersion</c> the server holds at submission time (the
/// one of the context); the client sends none. Without it any consent field set to true answers 400
/// (<see cref="CheckInValidationKeys.MarketingConsentUnavailable"/>).
/// </description></item>
/// <item><description>
/// <b>What is stored.</b> One <c>MarketingConsentRecord</c> per submission with a ticked channel: the channels ticked, the
/// normalized marketing e-mail, the time of the submission (UTC), the text version, the client IP and the booking, with the
/// e-mail not confirmed yet. It never touches the legacy flag of the guest (<c>Guest.MarketingConsent</c>).
/// </description></item>
/// <item><description>
/// <b>Errors.</b> 400 <c>validation_error</c> with the errors keyed by property name, as for the guests
/// (<c>MarketingEmail</c>, <c>MarketingConsentEmail</c>, <c>MarketingConsentWhatsApp</c>, <c>MarketingConsent</c>); the
/// messages are localized (Italian, English).
/// </description></item>
/// <item><description>
/// <b>Compatibility.</b> <see cref="MarketingConsent"/> keeps its behavior; a request that does not send the new properties
/// is read exactly as before.
/// </description></item>
/// </list>
/// </remarks>
public class PublicCheckInSubmitRequest
{
    /// <summary>Guests in record order: a head of family or group before its members; minors included.</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public List<StayGuestSubmitDto> Guests { get; set; } = [];

    /// <summary>
    /// Optional marketing consent (CO-15): accepted only when the context offered it (<c>marketingConsentVersion</c>). There
    /// is no consent for the Alloggiati registration: it is a legal obligation and the privacy notice is only shown (A5-15).
    /// Legacy single box, kept for clients that do not know the channels: it names no channel and no marketing e-mail, so it
    /// is recorded on the guest and in the consent history as before and never as a per-channel consent. New screens leave it
    /// <c>false</c> and send the channels below.
    /// </summary>
    public bool MarketingConsent { get; set; }

    /// <summary>
    /// The guest ticked the e-mail box of the consent step (NF-05). Default <c>false</c>; requires <see cref="MarketingEmail"/>.
    /// </summary>
    public bool MarketingConsentEmail { get; set; }

    /// <summary>
    /// The guest ticked the WhatsApp box of the consent step (NF-05). Default <c>false</c>; requires <see cref="MarketingEmail"/>
    /// (the confirmation is by e-mail for every channel). The request carries no WhatsApp number.
    /// </summary>
    public bool MarketingConsentWhatsApp { get; set; }

    /// <summary>
    /// The real address for marketing, typed in a field of its own (NF-05): never the booking contact (for Booking.com and
    /// Airbnb that is a forwarding alias), so the form must not prefill it from there. Required when
    /// <see cref="MarketingConsentEmail"/> or <see cref="MarketingConsentWhatsApp"/> is <c>true</c>
    /// (<see cref="CheckInValidationKeys.MarketingEmailRequired"/>): a plain address, at most 255 characters
    /// (<see cref="CheckInValidationKeys.MarketingEmailInvalid"/>). Ignored when no channel is ticked. Stored trimmed and in
    /// lowercase.
    /// </summary>
    public string? MarketingEmail { get; set; }
}

/// <summary>
/// One guest. Always required: kind, names, sex, date of birth, born in Italy or not, citizenship. Born in Italy: comune
/// and province; abroad: state. Document (kind or table code, number, place of issue) only for <c>SingleGuest</c>,
/// <c>HeadOfFamily</c> and <c>HeadOfGroup</c>. Codes are optional (official tables, when imported).
/// </summary>
public class StayGuestSubmitDto
{
    /// <summary><c>SingleGuest</c>, <c>HeadOfFamily</c>, <c>HeadOfGroup</c>, <c>FamilyMember</c> or <c>GroupMember</c>.</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(20, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string Type { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(AlloggiatiRecordRules.MaxFirstNameLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(AlloggiatiRecordRules.MaxLastNameLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Only <see cref="Gender.Male"/> or <see cref="Gender.Female"/>: the values Alloggiati Web accepts.</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public Gender? Gender { get; set; }

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public DateTime? DateOfBirth { get; set; }

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public bool? BornInItaly { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? BirthComuneCode { get; set; }

    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? BirthComuneName { get; set; }

    [MaxLength(2, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? BirthProvince { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? BirthCountryCode { get; set; }

    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? BirthCountryName { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? CitizenshipCode { get; set; }

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string CitizenshipName { get; set; } = string.Empty;

    /// <summary><c>Passport</c>, <c>IdentityCard</c> or <c>DriversLicense</c> when no document table is imported.</summary>
    [MaxLength(50, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? DocumentType { get; set; }

    [MaxLength(5, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? DocumentTypeCode { get; set; }

    [MaxLength(50, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? DocumentNumber { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? DocumentIssuePlaceCode { get; set; }

    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? DocumentIssuePlaceName { get; set; }

    public StayGuestInput ToInput() => new()
    {
        Type = Type,
        FirstName = FirstName,
        LastName = LastName,
        Gender = Gender,
        DateOfBirth = DateOfBirth,
        BornInItaly = BornInItaly,
        BirthComuneCode = BirthComuneCode,
        BirthComuneName = BirthComuneName,
        BirthProvince = BirthProvince,
        BirthCountryCode = BirthCountryCode,
        BirthCountryName = BirthCountryName,
        CitizenshipCode = CitizenshipCode,
        CitizenshipName = CitizenshipName,
        DocumentType = DocumentType,
        DocumentTypeCode = DocumentTypeCode,
        DocumentNumber = DocumentNumber,
        DocumentIssuePlaceCode = DocumentIssuePlaceCode,
        DocumentIssuePlaceName = DocumentIssuePlaceName,
    };
}

/// <summary>Length limits of the guest labels shared by the DTOs.</summary>
public static class StayGuestFieldLimits
{
    public const int Label = 100;
}

/// <summary>
/// A check-in link the host just generated (<c>POST /api/bookings/{id}/checkin/link</c>) or sent
/// (<c>POST .../checkin/resend-link</c>): the link to copy, always, and the real state of its email (CO-09).
/// </summary>
public class CheckInLinkResponse
{
    public string CheckInLink { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    /// <summary><c>NotRequested</c> (link to copy), <c>Queued</c> or <c>Failed</c> (with <see cref="EmailError"/>).</summary>
    public GuestCheckInLinkEmailStatus EmailStatus { get; set; }

    /// <summary>Stable reason of a failed email (<see cref="GuestCheckInLinkEmailErrors"/>).</summary>
    public string? EmailError { get; set; }
}

/// <summary>
/// The check-in link of a booking as the host sees it (<c>GET /api/bookings/{id}/checkin-session</c>). Every field but
/// <see cref="CanIssueLink"/> is null when no link was ever issued.
/// </summary>
public class CheckInSessionStatusResponse
{
    public Guid? SessionId { get; set; }

    /// <summary>Status at the time of the request: an open link past its expiry is <c>Scaduto</c> (A5-27).</summary>
    public string? Status { get; set; }

    public DateTime? IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the email was handed to the provider; null when it was not.</summary>
    public DateTime? SentAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>State of the link email; null for links issued before CO-09 (unknown).</summary>
    public GuestCheckInLinkEmailStatus? EmailStatus { get; set; }

    public string? EmailError { get; set; }

    /// <summary>
    /// A new link can be generated or sent: the booking is confirmed or checked in and the guest has not completed the
    /// check-in. The caller also needs <c>booking.write</c>.
    /// </summary>
    public bool CanIssueLink { get; set; }

    public static CheckInSessionStatusResponse From(GuestCheckInSession? session, bool bookingEligible, DateTime nowUtc) =>
        session is null
            ? new CheckInSessionStatusResponse { CanIssueLink = bookingEligible }
            : new CheckInSessionStatusResponse
            {
                SessionId = session.Id,
                Status = session.EffectiveStatus(nowUtc).ToString(),
                IssuedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt,
                SentAt = session.SentAt,
                CompletedAt = session.CompletedAt,
                EmailStatus = session.LinkEmailStatus,
                EmailError = session.LinkEmailStatus == GuestCheckInLinkEmailStatus.Failed ? session.LinkEmailError : null,
                CanIssueLink = bookingEligible && !session.IsCompleted,
            };
}

/// <summary>
/// Response from document upload endpoint with extracted fields for guest confirmation (NF-10, ADR-009-R22).
/// </summary>
public class DocumentUploadResponse
{
    /// <summary>True if extraction succeeded (MRZ or OCR).</summary>
    public bool Success { get; set; }

    /// <summary>Extraction method used (MRZ, OCR, or None).</summary>
    public string ExtractionMethod { get; set; } = string.Empty;

    /// <summary>Extracted fields for guest confirmation.</summary>
    public ExtractedDocumentFields? Fields { get; set; }

    /// <summary>Confidence score (0.0 to 1.0) of the extraction.</summary>
    public double Confidence { get; set; }

    /// <summary>True if check digits are valid (MRZ only, ADR-009-R23).</summary>
    public bool? CheckDigitsValid { get; set; }

    /// <summary>True if manual review is suggested (low confidence or invalid check digits).</summary>
    public bool RequiresManualReview { get; set; }
}

/// <summary>
/// Extracted document fields from MRZ or OCR (NF-10).
/// </summary>
public class ExtractedDocumentFields
{
    public string? DocumentType { get; set; }
    public string? Surname { get; set; }
    public string? GivenNames { get; set; }
    public string? FullName { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public string? Sex { get; set; }
    public string? IssuingState { get; set; }
}

/// <summary>
/// Response from selfie upload endpoint with biometric verification outcome (NF-11, ADR-013).
/// NEVER includes image data or embeddings per ADR-013-R2 (immediate deletion guarantee).
/// </summary>
public class SelfieUploadResponse
{
    /// <summary>Biometric verification outcome: Accept, Escalate, or Reject (ADR-013-R4).</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Number of attempts made in this session (max 3 per ADR-013-R5).</summary>
    public int Attempts { get; set; }

    /// <summary>Maximum attempts allowed.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>True if max attempts reached and session auto-escalated to operator queue (ADR-013-R5).</summary>
    public bool AutoEscalated { get; set; }

    /// <summary>Message key for localized feedback to the guest.</summary>
    public string? MessageKey { get; set; }
}
