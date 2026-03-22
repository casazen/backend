using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.OTA;

public class VrboAdapter(HttpClient httpClient, ILogger<VrboAdapter> logger) : IChannelAdapter
{
    private readonly string _baseUrl = "https://api.vrbo.com/v1";

    public string Platform => "VRBO";

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
            logger.LogError(ex, "VRBO validation failed");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        var bookings = new List<OtaBookingModel>();
        try
        {
            logger.LogInformation("Fetching VRBO bookings for {PropertyId}", externalPropertyId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching VRBO bookings");
        }
        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        try
        {
            logger.LogInformation("Updating VRBO availability");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating VRBO availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        try
        {
            logger.LogInformation("Updating VRBO pricing");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating VRBO pricing");
            return false;
        }
    }
}
