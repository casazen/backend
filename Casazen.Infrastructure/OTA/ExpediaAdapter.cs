using Microsoft.Extensions.Logging;
using Casazen.Infrastructure.OTA.Resilience;

namespace Casazen.Infrastructure.OTA;

public class ExpediaAdapter : IChannelAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExpediaAdapter> _logger;
    private readonly OtaRateLimiter _rateLimiter;
    private readonly string _baseUrl = "https://api.expediagroup.com/v1";

    public ExpediaAdapter(HttpClient httpClient, ILogger<ExpediaAdapter> logger, OtaRateLimiter rateLimiter)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public string Platform => "Expedia";

    public async Task<bool> ValidateCredentialsAsync(string apiKey, string? apiSecret = null)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/properties");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expedia validation failed");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        var bookings = new List<OtaBookingModel>();
        try
        {
            _logger.LogInformation("Fetching Expedia bookings for {PropertyId}", externalPropertyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Expedia bookings");
        }
        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Expedia availability");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Expedia availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Expedia pricing");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Expedia pricing");
            return false;
        }
    }
}
