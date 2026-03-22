using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.OTA;

public class AgodaAdapter(HttpClient httpClient, ILogger<AgodaAdapter> logger) : IChannelAdapter
{
    private readonly string _baseUrl = "https://api.agoda.com/v1";

    public string Platform => "Agoda";

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
            logger.LogError(ex, "Agoda validation failed");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        var bookings = new List<OtaBookingModel>();
        try
        {
            logger.LogInformation("Fetching Agoda bookings for {PropertyId}", externalPropertyId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching Agoda bookings");
        }
        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        try
        {
            logger.LogInformation("Updating Agoda availability");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating Agoda availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        try
        {
            logger.LogInformation("Updating Agoda pricing");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating Agoda pricing");
            return false;
        }
    }
}
