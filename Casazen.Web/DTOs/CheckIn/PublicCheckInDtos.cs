using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.CheckIn;

public class PublicCheckInContextResponse
{
    public Guid SessionId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Status { get; set; } = string.Empty;
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
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentIssuingCountry { get; set; } = string.Empty;
    public string PlaceOfBirth { get; set; } = string.Empty;
}

public class PublicCheckInSubmitRequest
{
    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    public DateTime? DateOfBirth { get; set; }

    [Required, MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;

    [Required]
    public Gender? Gender { get; set; }

    [Required, MaxLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string DocumentIssuingCountry { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string PlaceOfBirth { get; set; } = string.Empty;

    [Required]
    public bool GdprConsent { get; set; }

    public bool MarketingConsent { get; set; }
}

public class ResendCheckInLinkResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public class CheckInSessionStatusResponse
{
    public Guid? SessionId { get; set; }
    public string? Status { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
