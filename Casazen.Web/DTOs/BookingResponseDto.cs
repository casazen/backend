namespace Casazen.Web.DTOs;

public class BookingResponseDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int NumberOfGuests { get; set; }

    /// <summary>Adults and minors among <see cref="NumberOfGuests"/> (every guest an adult when the split is unknown).</summary>
    public int NumberOfAdults { get; set; }
    public int NumberOfChildren { get; set; }

    public decimal TotalPrice { get; set; }

    /// <summary>Lodging plus <see cref="CleaningFee"/>, tourist tax excluded.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Cleaning fee included in <see cref="BasePrice"/>: the lodging is <c>BasePrice - CleaningFee</c>.</summary>
    public decimal CleaningFee { get; set; }

    public decimal TouristTax { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = string.Empty;

    /// <summary>Reason the host wrote when cancelling (host only).</summary>
    public string? CancellationNote { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SpecialRequests { get; set; } = string.Empty;
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
