namespace Casazen.Web.DTOs;

public class CalendarBookingDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public Guid GuestId { get; set; }
    /// <summary>Check-in day (stay date, no time zone: <c>2026-09-30T00:00:00</c>), MO-06.</summary>
    public DateTime CheckInDate { get; set; }

    /// <summary>Check-out day (stay date, no time zone), MO-06.</summary>
    public DateTime CheckOutDate { get; set; }
    public DateTime CheckInDateUtc { get; set; }
    public DateTime CheckOutDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int NumberOfGuests { get; set; }
    public decimal TotalPrice { get; set; }
    public string GuestName { get; set; } = string.Empty;
}
