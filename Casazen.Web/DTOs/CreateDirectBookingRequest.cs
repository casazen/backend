using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public class CreateDirectBookingGuestRequest
{
    [Required(ErrorMessage = "First name is required")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Country is required")]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
}

public class CreateDirectBookingConsentRequest
{
    [Required]
    public bool DataProcessing { get; set; }

    [Required(ErrorMessage = "Consent version is required")]
    [MaxLength(100)]
    public string ConsentVersion { get; set; } = string.Empty;
}

public class CreateDirectBookingRequest
{
    [Required(ErrorMessage = "Property is required")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "Check-in date is required")]
    public DateTime CheckInDate { get; set; }

    [Required(ErrorMessage = "Check-out date is required")]
    public DateTime CheckOutDate { get; set; }

    [Range(1, 100, ErrorMessage = "Number of adults must be at least 1")]
    public int NumberOfAdults { get; set; }

    [Range(0, 100, ErrorMessage = "Number of children cannot be negative")]
    public int NumberOfChildren { get; set; }

    [Required(ErrorMessage = "Guest information is required")]
    public CreateDirectBookingGuestRequest Guest { get; set; } = null!;

    [Required(ErrorMessage = "Consent is required")]
    public CreateDirectBookingConsentRequest Consent { get; set; } = null!;

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    /// <summary>Payment option: Immediate, OnCancellationDeadline, or OnSite.</summary>
    [Required]
    public PaymentOption PaymentOption { get; set; } = PaymentOption.Immediate;
}
