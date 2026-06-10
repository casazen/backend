using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.CheckIn;

public class CheckInContextDto
{
    public Guid BookingId { get; set; }
    public Guid GuestId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public CheckInGuestDto Guest { get; set; } = new();
    public bool DataComplete { get; set; }
}

public class CheckInGuestDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string PlaceOfBirth { get; set; } = string.Empty;
    public string Nationality { get; set; } = string.Empty;
    public Gender? Gender { get; set; }
    public GuestDocumentType? DocumentType { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentExpiryDate { get; set; }
    public string DocumentIssuingCountry { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? DocumentScanUrl { get; set; }
}

public class SubmitGuestCheckInRequest
{
    [Required]
    public DateTime? DateOfBirth { get; set; }

    [Required, MaxLength(100)]
    public string PlaceOfBirth { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;

    [Required]
    public Gender Gender { get; set; }

    [Required]
    public GuestDocumentType DocumentType { get; set; }

    [Required, MaxLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? DocumentExpiryDate { get; set; }

    [Required, MaxLength(100)]
    public string DocumentIssuingCountry { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(50)]
    public string City { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;

    [Required]
    public bool ConsentAccepted { get; set; }
}

public class GuestCheckInDataResponse
{
    public bool DataComplete { get; set; }
}

public class GuestDocumentUploadResponse
{
    public string DocumentScanUrl { get; set; } = string.Empty;
}
