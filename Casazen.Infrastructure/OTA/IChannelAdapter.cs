namespace Casazen.Infrastructure.OTA;

public interface IChannelAdapter
{
    string Platform { get; }
    Task<bool> ValidateCredentialsAsync(string apiKey, string? apiSecret = null);
    Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate);
    Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable);
    Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price);
}

public class OtaBookingModel
{
    public string ExternalBookingId { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string GuestEmail { get; set; } = string.Empty;
    public decimal TotalPrice { get; set; }
    public string Status { get; set; } = string.Empty;
}