namespace Casazen.Infrastructure.OTA;

public interface IChannelAdapter
{
    string Platform { get; }
    Task<bool> ValidateCredentialsAsync(string apiKey, string? apiSecret = null);
    Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate);
    Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable);
    Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price);

    /// <summary>
    /// Updates pricing for a date range on the OTA platform.
    /// Implements exponential backoff retry logic (max 3 retries) internally.
    /// Returns a dictionary mapping DateOnly values to sync success status.
    /// </summary>
    /// <param name="externalPropertyId">The external property ID on the OTA platform</param>
    /// <param name="pricesByDate">Dictionary of dates and their corresponding prices</param>
    /// <returns>Dictionary mapping DateOnly to boolean sync status (true = succeeded, false = failed)</returns>
    Task<Dictionary<DateOnly, bool>> UpdatePricingBatchAsync(string externalPropertyId, Dictionary<DateOnly, decimal> pricesByDate);
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