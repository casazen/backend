using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Identity;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

public class PublicCheckInContextResponse
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HouseRules { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HouseRulesVersion { get; set; }

    public bool Completed { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The slug of the org that owns the stay (NF-140), sent with every usable link, a completed one too: it is public data, not personal data, so A5-28 does not hide it. The page on an
    /// agency's own domain compares it with the org of the domain, so the check-in of another agency never opens there under the domain's name. Unlike <see cref="PrivacyNoticeOrgSlug"/>
    /// it does not depend on a published privacy notice.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrgSlug { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SessionId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertyName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CheckInDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CheckOutDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeclaredGuests { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PublicCheckInGuestPrefill>? Guests { get; set; }

    /// <summary>Official Alloggiati tables imported: the form offers their codes (e.g. <c>Comuni</c>, <c>Stati</c>, <c>Documenti</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AlloggiatiCodeTable>? AvailableCodeTables { get; set; }

    /// <summary>Version of the privacy notice (art. 13 GDPR) the portal shows; absent when not configured (CO-15).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivacyNoticeVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivacyNoticeOrgSlug { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MarketingConsentVersion { get; set; }

    /// <summary>
    /// The answer to «Ti serve la fattura?» is already <c>yes</c> (E06/F02/US01/T03, FIS-01): the form starts with the question answered. Absent once
    /// the check-in is completed. The invoice data itself is write-only from this page: the link is anonymous, so the stored fiscal code, VAT number,
    /// address and PEC are never returned, only this flag and <see cref="HasInvoiceData"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InvoiceRequested { get; set; }

    /// <summary>The booking already has the data of an invoice recipient (the host entered it, or Octorate had it); never its content. Absent once the check-in is completed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasInvoiceData { get; set; }

    /// <summary>
    /// The guest fields the regime of the property requires, the page asks only these (data minimization), as names of the properties of a guest
    /// (<see cref="StayGuestSubmitDto"/>): the statistical fields the region requires (<c>TourismType</c>, <c>TravelWay</c>, <c>EducationLevel</c>;
    /// none when the comune is unknown or the region requires none) and, for a property in canton Ticino (NF-132), <c>DocumentType</c> and
    /// <c>DocumentNumber</c> (of every adult, family and group members included, or of every minor of a stay without adults, in a group of six or
    /// more of its leader only: required),
    /// <c>ResidenceStreet</c>, <c>ResidencePostalCode</c>, <c>ResidenceCityName</c> and <c>ResidenceCountryName</c> (the home and the state of
    /// residence, as the residence block asks it: asked, optional).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredGuestFields { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AskVehiclePlate { get; set; }

    /// <summary>
    /// The language to open the page in (NF-40): the one the guest saved, otherwise the first supported language of the browser
    /// (<c>Accept-Language</c>), otherwise <c>it</c>. Sent with an open session only.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SupportedLanguages { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DocumentNotArchived { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicIdentityVerificationStateDto? IdentityVerification { get; set; }
}

/// <summary>
/// Where the identity verification of the session stands (E03/F04/US02/T04): <c>step</c> is <c>consent</c>, <c>document</c>, <c>selfie</c>,
/// <c>presence</c> (the self-hosted flow, E70-E74), <c>done</c> or <c>operator</c>; <c>result</c> the last one (<c>ok</c>, <c>retry</c>, <c>operator</c>) or null;
/// only the guest at <c>verifyingGuestPosition</c> (the one who leaves the document) is asked for a selfie; <c>operatorPathOpen</c> is always true.
/// </summary>
public sealed class PublicIdentityVerificationStateDto
{
    public string Step { get; set; } = string.Empty;
    public string? Result { get; set; }
    public bool BiometricOffered { get; set; }
    public bool ConsentRecorded { get; set; }
    public bool ConsentRefused { get; set; }
    public bool DocumentChecked { get; set; }
    public int AttemptsRemaining { get; set; }
    public bool OperatorRequested { get; set; }
    public bool OperatorPathOpen { get; set; }
    public bool DocumentNotArchived { get; set; }
    public int VerifyingGuestPosition { get; set; }

    public IReadOnlyList<string> DocumentSides { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentSideNeeded { get; set; }

    public bool SelfHosted { get; set; }

    public bool Opened { get; set; }

    public string? NoticeVersion { get; set; }

    public string? Choice { get; set; }

    public string? FormatId { get; set; }

    /// <summary>The one guided retry of a correctable defect is still available.</summary>
    public bool GuidedRetryAvailable { get; set; }

    public bool Closed { get; set; }

    /// <summary>
    /// What the guest sees of the closed verification: <c>ok</c>, <c>retry</c> or <c>operator</c>; null before it is decided, and for a verification the retention closed as
    /// abandoned while nobody has it (<see cref="Closed"/> with no outcome: closed with no positive outcome, no operator was told; the guest can still ask for a person).
    /// </summary>
    public string? Outcome { get; set; }

    public static PublicIdentityVerificationStateDto From(IdentityVerificationState state) => new()
    {
        Step = state.Step,
        Result = state.Result,
        BiometricOffered = state.BiometricOffered,
        ConsentRecorded = state.ConsentRecorded,
        ConsentRefused = state.ConsentRefused,
        DocumentChecked = state.DocumentChecked,
        AttemptsRemaining = state.AttemptsRemaining,
        OperatorRequested = state.OperatorRequested,
        OperatorPathOpen = state.OperatorPathOpen,
        DocumentNotArchived = state.DocumentNotArchived,
        VerifyingGuestPosition = state.VerifyingGuestPosition,
        DocumentSides = state.DocumentSides ?? [],
        DocumentSideNeeded = state.DocumentSideNeeded,
        SelfHosted = state.SelfHosted,
        Opened = state.Opened,
        NoticeVersion = state.NoticeVersion,
        Choice = state.Choice,
        FormatId = state.FormatId,
        GuidedRetryAvailable = state.GuidedRetryAvailable,
        Closed = state.Closed,
        Outcome = state.Outcome,
    };
}

public sealed class BiometricConsentRequest
{
    public bool? ConsentGiven { get; set; }

    /// <summary>
    /// The version of the text the guest read, as <c>GET .../legal-texts</c> served it: at most <see cref="BiometricConsentRules.VersionMaxLength"/> characters
    /// (400 <c>biometric_consent_version_required</c> otherwise), and a version the registry serves now (422 <c>biometric_consent_version_unknown</c> otherwise).
    /// </summary>
    public string? ConsentVersion { get; set; }
}

public sealed class CheckInLegalTextsResponse
{
    public string Language { get; set; } = string.Empty;

    public IReadOnlyList<CheckInLegalTextDto> Texts { get; set; } = [];

    public static CheckInLegalTextsResponse From(string language, IReadOnlyList<CheckInLegalText> texts) => new()
    {
        Language = language,
        Texts = [.. texts.Select(CheckInLegalTextDto.From)],
    };
}

public sealed class CheckInLegalTextDto
{
    public string Kind { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public bool IsDraft { get; set; }

    public string ContentHtml { get; set; } = string.Empty;

    public static CheckInLegalTextDto From(CheckInLegalText text) => new()
    {
        Kind = text.Kind,
        Version = text.Version,
        Language = text.Language,
        IsDraft = text.IsDraft,
        ContentHtml = text.ContentHtml,
    };
}

public sealed class BiometricConsentResponse
{
    public const string RecordedResult = "consent_recorded";

    public bool ConsentRecorded { get; set; }
    public string Result { get; set; } = string.Empty;
    public bool BiometricOffered { get; set; }
    public bool OperatorPathOpen { get; set; } = true;
    public PublicIdentityVerificationStateDto State { get; set; } = new();
}

public sealed class IdentityOpenRequest
{
    public string? NoticeVersion { get; set; }
}

public sealed class IdentityOpenResponse
{
    public PublicIdentityVerificationStateDto State { get; set; } = new();
}

public sealed class OperatorRequestResponse
{
    public string Result { get; set; } = string.Empty;
    public bool OperatorPathOpen { get; set; } = true;
    public PublicIdentityVerificationStateDto State { get; set; } = new();
}

public class PublicCheckInLanguageRequest
{
    public string? Language { get; set; }
}

public class PublicCheckInLanguageResponse
{
    public string Language { get; set; } = string.Empty;
}

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

    public string? DocumentNumberMasked { get; set; }

    public string? DocumentIssuePlaceCode { get; set; }
    public string DocumentIssuePlaceName { get; set; } = string.Empty;
    public DateOnly? DocumentIssuedOn { get; set; }
    public DateOnly? DocumentExpiresOn { get; set; }
    public bool? ResidenceInItaly { get; set; }
    public string? ResidenceComuneCode { get; set; }
    public string? ResidenceComuneName { get; set; }
    public string? ResidenceProvince { get; set; }
    public string? ResidenceCountryCode { get; set; }
    public string? ResidenceCountryName { get; set; }

    public string? ResidenceStreet { get; set; }
    public string? ResidencePostalCode { get; set; }
    public string? ResidenceCityName { get; set; }
    public string? TourismType { get; set; }
    public string? TravelWay { get; set; }
    public string? EducationLevel { get; set; }

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
        DocumentIssuedOn = prefill.DocumentIssuedOn,
        DocumentExpiresOn = prefill.DocumentExpiresOn,
        ResidenceInItaly = prefill.ResidenceInItaly,
        ResidenceComuneCode = prefill.ResidenceComuneCode,
        ResidenceComuneName = prefill.ResidenceComuneName,
        ResidenceProvince = prefill.ResidenceProvince,
        ResidenceCountryCode = prefill.ResidenceCountryCode,
        ResidenceCountryName = prefill.ResidenceCountryName,
        ResidenceStreet = prefill.ResidenceStreet,
        ResidencePostalCode = prefill.ResidencePostalCode,
        ResidenceCityName = prefill.ResidenceCityName,
        TourismType = prefill.TourismType,
        TravelWay = prefill.TravelWay,
        EducationLevel = prefill.EducationLevel,
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

    public bool? PrivacyNoticeAcknowledged { get; set; }

    [MaxLength(64)]
    public string? HouseRulesVersion { get; set; }

    public bool MarketingConsentEmail { get; set; }

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

    /// <summary>
    /// The optional answer to «Ti serve la fattura?» (E06/F02/US01/T03, FIS-01): a request without this block, or with <c>needInvoice</c> false,
    /// changes nothing and never blocks the check-in. With <c>needInvoice</c> true the data typed in it is saved as the invoice data of the booking
    /// (source CheckIn, never over what the host wrote); a data that is not valid answers 422 with the field and saves nothing, so the guest
    /// corrects and sends again.
    /// </summary>
    public PublicCheckInInvoiceDto? Invoice { get; set; }

    /// <summary>
    /// The plate of the car (NF-132), asked once for the stay where the context says <c>askVehiclePlate</c>; optional. Not sent (<c>null</c>):
    /// the stored plate stays; an empty text clears it. Spaces are removed and the letters upper-cased; letters, digits and hyphens only, at most
    /// 20 (<c>VehiclePlate</c>: <c>CheckInVehiclePlateInvalid</c>). Write-only: the page never reads it back.
    /// </summary>
    [MaxLength(64, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? VehiclePlate { get; set; }
}

/// <summary>
/// The «Ti serve la fattura?» block of the check-in (E06/F02/US01/T03, FIS-01). Everything but <see cref="NeedInvoice"/> is optional and
/// write-only: nothing of it is ever returned by the public page. The formats are checked by the invoice data service (fiscal code through
/// <c>ItalianFiscalCode</c>, VAT number with its check digit when Italian, recipient code, PEC); a blank text is a field the guest left empty.
/// </summary>
public class PublicCheckInInvoiceDto
{
    public bool NeedInvoice { get; set; }

    public string? RecipientKind { get; set; }

    public string? Name { get; set; }

    public string? FiscalCode { get; set; }

    public string? VatNumber { get; set; }

    public string? AddressStreet { get; set; }

    public string? AddressCivic { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Province { get; set; }

    public string? CountryCode { get; set; }

    public string? SdiRecipientCode { get; set; }

    public string? Pec { get; set; }

    /// <summary>
    /// The block as the service reads it. A «no» (<see cref="NeedInvoice"/> false) is nothing: the rest of the block is not even read, so whatever it
    /// holds never blocks the check-in. A text that is blank is a field left empty (not sent), so the guest can never clear what the host wrote.
    /// With a «yes», a kind of recipient that is neither <c>Guest</c> nor <c>Company</c> is 422 <c>invoice_data_field_invalid</c> on <c>recipientKind</c>.
    /// </summary>
    public GuestCheckInInvoice ToInput()
    {
        if (!NeedInvoice)
            return new GuestCheckInInvoice { NeedInvoice = false };

        if (!InvoiceDataFormats.TryParseRecipientKind(RecipientKind, out var kind))
        {
            throw InvoiceDataErrorCodes.ToException(
                [new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.RecipientKind)]);
        }

        return new GuestCheckInInvoice
        {
            NeedInvoice = NeedInvoice,
            Data = new SaveInvoiceDataCommand
            {
                RecipientKind = kind,
                Name = Typed(Name),
                FiscalCode = Typed(FiscalCode),
                VatNumber = Typed(VatNumber),
                AddressStreet = Typed(AddressStreet),
                AddressCivic = Typed(AddressCivic),
                PostalCode = Typed(PostalCode),
                City = Typed(City),
                Province = Typed(Province),
                CountryCode = Typed(CountryCode),
                SdiRecipientCode = Typed(SdiRecipientCode),
                Pec = Typed(Pec),
            },
        };
    }

    private static string? Typed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// One guest. Always required: kind, names, sex, date of birth, born in Italy or not, citizenship. Born in Italy: comune
/// and province; abroad: state. Document (kind or table code, number, place of issue) only for <c>SingleGuest</c>,
/// <c>HeadOfFamily</c> and <c>HeadOfGroup</c>; for a property in canton Ticino (NF-132) the kind and number of the document also of every other
/// guest the police file notifies on their own row (every adult, or every minor of a stay without adults), their place of issue optional. Codes
/// are optional (official tables, when imported).
/// </summary>
public class StayGuestSubmitDto
{
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(20, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string Type { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(AlloggiatiRecordRules.MaxFirstNameLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(AlloggiatiRecordRules.MaxLastNameLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string LastName { get; set; } = string.Empty;

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

    public DateOnly? DocumentIssuedOn { get; set; }

    public DateOnly? DocumentExpiresOn { get; set; }

    public bool? ResidenceInItaly { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceComuneCode { get; set; }

    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceComuneName { get; set; }

    [MaxLength(2, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceProvince { get; set; }

    [MaxLength(9, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceCountryCode { get; set; }

    [MaxLength(StayGuestFieldLimits.Label, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceCountryName { get; set; }

    [MaxLength(StayGuest.ResidenceStreetMaxLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceStreet { get; set; }

    [MaxLength(StayGuest.ResidencePostalCodeMaxLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidencePostalCode { get; set; }

    [MaxLength(StayGuest.ResidenceCityNameMaxLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? ResidenceCityName { get; set; }

    [MaxLength(IstatGuestFieldLists.MaxCodeLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? TourismType { get; set; }

    [MaxLength(IstatGuestFieldLists.MaxCodeLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? TravelWay { get; set; }

    [MaxLength(IstatGuestFieldLists.MaxCodeLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? EducationLevel { get; set; }

    [MaxLength(TouristTaxExemptionReason.CodeMaxLength, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? TaxExemptionCode { get; set; }

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
        DocumentIssuedOn = DocumentIssuedOn,
        DocumentExpiresOn = DocumentExpiresOn,
        ResidenceInItaly = ResidenceInItaly,
        ResidenceComuneCode = ResidenceComuneCode,
        ResidenceComuneName = ResidenceComuneName,
        ResidenceProvince = ResidenceProvince,
        ResidenceCountryCode = ResidenceCountryCode,
        ResidenceCountryName = ResidenceCountryName,
        ResidenceStreet = ResidenceStreet,
        ResidencePostalCode = ResidencePostalCode,
        ResidenceCityName = ResidenceCityName,
        TourismType = TourismType,
        TravelWay = TravelWay,
        EducationLevel = EducationLevel,
        TaxExemptionCode = TaxExemptionCode,
    };
}

public static class StayGuestFieldLimits
{
    public const int Label = 100;
}

public class CheckInLinkResponse
{
    public string CheckInLink { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    public GuestCheckInLinkEmailStatus EmailStatus { get; set; }

    public string? EmailError { get; set; }
}

public class CheckInSessionStatusResponse
{
    public Guid? SessionId { get; set; }

    public string? Status { get; set; }

    public DateTime? IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public GuestCheckInLinkEmailStatus? EmailStatus { get; set; }

    public string? EmailError { get; set; }

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

public class DocumentUploadResponse
{
    public bool Success { get; set; }

    public string ExtractionMethod { get; set; } = string.Empty;

    public ExtractedDocumentFields? Fields { get; set; }

    public double Confidence { get; set; }

    public bool? CheckDigitsValid { get; set; }

    public bool RequiresManualReview { get; set; }

    public bool Acceptable { get; set; }

    public IReadOnlyList<string> GuidanceCodes { get; set; } = [];

    public bool DocumentStored { get; set; }

    public bool DocumentNotArchived { get; set; }

    public string Side { get; set; } = DocumentSides.Single;

    public IReadOnlyList<string> Sides { get; set; } = [];
}

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
/// E03/F04/US02/T04: <c>{ result: ok | retry | operator, guidanceCodes, attemptsRemaining, operatorPathOpen, lockCode? }</c>; the lock code only
/// on <c>ok</c> (and only when the property has one).
/// </summary>
public class SelfieUploadResponse
{
    /// <summary><c>ok</c> (passed), <c>retry</c> (take the selfie, or the document, again) or <c>operator</c> (a person verifies).</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>What to change for a retry (<c>use_live_camera</c>, <c>retake_selfie</c>, <c>move_closer</c>, ...): codes the page translates.</summary>
    public IReadOnlyList<string> GuidanceCodes { get; set; } = [];

    public int AttemptsRemaining { get; set; }

    /// <summary>The guest can always ask for a person (GDPR art. 22).</summary>
    public bool OperatorPathOpen { get; set; } = true;

    /// <summary>The lock code of the property, only on <c>ok</c>; it is e-mailed to the guest at the same time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LockCode { get; set; }

    /// <summary>
    /// Why an <c>ok</c> came with no <see cref="LockCode"/> although the property has one (E25/F01/US01/T01): <c>tourist_tax_due</c> while the organization asks
    /// for the tourist tax before the code and the stay owes it online, unpaid, not exempt and not marked «paid at the property». The guest pays on the
    /// tourist tax endpoints and sends the selfie step again (a passed verification is not run again): the code comes with that answer. Absent otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LockCodeHeld { get; set; }
}

/// <summary>
/// Answer of <c>POST /api/public/checkin/{token}/read-document</c>. The photo itself is never returned. With the self-hosted flow on (<c>Features:CheckInIdentitySelfHosted</c>,
/// E71/F01/US03/T01, NF-190) it also carries <c>fields</c> (what the zones of the card gave, each with its side, zone and confidence), <c>missing</c> (the check-in fields the guest
/// has still to type) and <c>source</c> (<c>local</c>: nothing left the process); with it off none of the three is in the answer, which is then exactly the one of before.
/// </summary>
public class CheckInDocumentReadResponse
{
    public const string LocalSource = "local";

    public bool Read { get; set; }
    public string? Reason { get; set; }
    public CheckInDocumentFields? Guest { get; set; }
    public string? Side { get; set; }
    public int? GuestPosition { get; set; }
    public bool ConfirmGuest { get; set; }

    /// <summary>The values read by zone: <c>name</c>, <c>value</c>, <c>side</c>, <c>zone</c>, <c>confidence</c> and <c>source</c> (<c>mrz</c> or <c>viz</c>). Personal data for the guest who sent the photo: never stored or logged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CheckInZonedFieldDto>? Fields { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Missing { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    public CheckInDocumentReadResponse WithZoned(IReadOnlyList<ZonedField> fields, IReadOnlyList<string> missing)
    {
        Fields = [.. fields.Select(CheckInZonedFieldDto.From)];
        Missing = missing;
        Source = LocalSource;
        return this;
    }

    public static CheckInDocumentReadResponse From(CheckInDocumentRead read) => new()
    {
        Read = read.Read,
        Reason = read.Reason,
        Guest = read.Fields is { } fields ? fields with
        {
            Citizenship = TicinoCountryCodes.DescriptionFromAlpha3(fields.Citizenship) ?? fields.Citizenship,
        } : null,
        Side = read.Side,
    };
}

/// <summary>
/// One value of the zoned prefill (<c>fields</c> of <c>read-document</c>, E71/F01/US03/T01, NF-190): what it is (<c>name</c>, a code of <c>ZonedFieldNames</c>), the <c>value</c>, the
/// <c>side</c> of the document it was read on (<c>front</c>, <c>back</c> or <c>single</c>), the <c>zone</c> of the format it was read from (the name of a field zone, or <c>mrz</c>), how far
/// it is trusted (<c>confidence</c>, 0 to 1) and where it comes from (<c>source</c>: <c>mrz</c> with its check digits, or <c>viz</c>, the printed zone). Personal data that goes to the
/// guest who sent the photo and nowhere else: <see cref="ToString"/> leaves the value out.
/// </summary>
public sealed class CheckInZonedFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Source { get; set; } = string.Empty;

    public static CheckInZonedFieldDto From(ZonedField field) => new()
    {
        Name = field.Name,
        Value = field.Value,
        Side = DocumentSides.CodeOf(field.Side),
        Zone = field.ZoneId,
        Confidence = field.Confidence,
        Source = field.Source,
    };

    public override string ToString() => $"CheckInZonedFieldDto {{ Name = {Name}, Side = {Side}, Zone = {Zone}, Source = {Source} }}";
}
