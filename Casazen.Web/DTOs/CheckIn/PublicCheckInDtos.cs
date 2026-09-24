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
public class PublicCheckInSubmitRequest
{
    /// <summary>Guests in record order: a head of family or group before its members; minors included.</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public List<StayGuestSubmitDto> Guests { get; set; } = [];

    /// <summary>Must be true (checked by the controller: <c>[Required]</c> cannot reject <c>false</c>).</summary>
    [Required]
    public bool GdprConsent { get; set; }

    public bool MarketingConsent { get; set; }
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

public class ResendCheckInLinkResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? CheckInLink { get; set; }
}

public class CheckInSessionStatusResponse
{
    public Guid? SessionId { get; set; }
    public string? Status { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
