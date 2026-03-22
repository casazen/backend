using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.OTA;

public class ExpediaAdapter(HttpClient httpClient, ILogger<ExpediaAdapter> logger) : IChannelAdapter
{
    private readonly string _baseUrl = "https://api.expediagroup.com/v1";

    public string Platform => "Expedia";

    public async Task<bool> ValidateCredentialsAsync(string apiKey, string? apiSecret = null)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/properties");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Expedia validation failed");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        var bookings = new List<OtaBookingModel>();
        try
        {
            logger.LogInformation("Fetching Expedia bookings for {PropertyId}", externalPropertyId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching Expedia bookings");
        }
        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        try
        {
            logger.LogInformation("Updating Expedia availability");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating Expedia availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        try
        {
            logger.LogInformation("Updating Expedia pricing");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating Expedia pricing");
            return false;
        }
    }
}
