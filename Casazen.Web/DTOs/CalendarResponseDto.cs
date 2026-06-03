namespace Casazen.Web.DTOs;

public class CalendarResponseDto
{
    public string Timezone { get; set; } = string.Empty;
    public int UtcOffsetMinutes { get; set; }
    public List<CalendarBookingDto> Bookings { get; set; } = new();
}
