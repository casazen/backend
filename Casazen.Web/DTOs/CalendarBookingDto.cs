namespace Casazen.Web.DTOs;

public class CalendarBookingDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public Guid GuestId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public DateTime CheckInDateUtc { get; set; }
    public DateTime CheckOutDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int NumberOfGuests { get; set; }
    public decimal TotalPrice { get; set; }
    public string GuestName { get; set; } = string.Empty;
}
