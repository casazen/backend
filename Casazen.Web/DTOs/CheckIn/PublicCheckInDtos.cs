using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicCheckInGuestPrefill? GuestPrefill { get; set; }
}

public class PublicCheckInGuestPrefill
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string Nationality { get; set; } = string.Empty;
    public Gender? Gender { get; set; }

    /// <summary>Document number on file with only its last characters visible (e.g. <c>*****567</c>); never the full number.</summary>
    public string? DocumentNumberMasked { get; set; }

    public string DocumentIssuingCountry { get; set; } = string.Empty;
    public string PlaceOfBirth { get; set; } = string.Empty;
}

/// <summary>
/// Guest data submitted from the public portal. Every <c>[Required]</c> property must also be sent by the frontend
/// (<c>PublicCheckInSubmitRequest</c> in <c>src/types/public-checkin.types.ts</c>): both sides have a contract test.
/// Validation messages are SharedResources keys (<see cref="CheckInValidationKeys"/>).
/// </summary>
public class PublicCheckInSubmitRequest
{
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(100, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(100, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public DateTime? DateOfBirth { get; set; }

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(100, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string Nationality { get; set; } = string.Empty;

    /// <summary>Only <see cref="Gender.Male"/> or <see cref="Gender.Female"/>: the values Alloggiati Web accepts.</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public Gender? Gender { get; set; }

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(50, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string DocumentType { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(50, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(100, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string DocumentIssuingCountry { get; set; } = string.Empty;

    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    [MaxLength(100, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string PlaceOfBirth { get; set; } = string.Empty;

    /// <summary>Must be true (checked by the controller: <c>[Required]</c> cannot reject <c>false</c>).</summary>
    [Required]
    public bool GdprConsent { get; set; }

    public bool MarketingConsent { get; set; }
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
