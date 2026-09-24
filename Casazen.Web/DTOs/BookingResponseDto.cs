namespace Casazen.Web.DTOs;

public class BookingResponseDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int NumberOfGuests { get; set; }
    public decimal TotalPrice { get; set; }
    public decimal BasePrice { get; set; }
    public decimal TouristTax { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SpecialRequests { get; set; } = string.Empty;

    /// <summary>Immediate, OnCancellationDeadline or OnSite.</summary>
    public string PaymentOption { get; set; } = string.Empty;

    /// <summary>
    /// Open "pay at the property" request (BK-06, D5): <c>AwaitingGuestEmail</c> or <c>AwaitingHostApproval</c>;
    /// <c>null</c> for every other booking.
    /// </summary>
    public string? OnSiteRequestState { get; set; }

    /// <summary>Deadline of an open "pay at the property" request (the host's answer once the email is confirmed).</summary>
    public DateTime? RequestExpiresAt { get; set; }

    public BookingGuestDto Guest { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class BookingGuestDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}
