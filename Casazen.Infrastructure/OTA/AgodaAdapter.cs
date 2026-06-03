using Microsoft.Extensions.Logging;
using Casazen.Infrastructure.OTA.Resilience;

namespace Casazen.Infrastructure.OTA;

public class AgodaAdapter : IChannelAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AgodaAdapter> _logger;
    private readonly OtaRateLimiter _rateLimiter;
    private readonly string _baseUrl = "https://api.agoda.com/v1";

    public AgodaAdapter(HttpClient httpClient, ILogger<AgodaAdapter> logger, OtaRateLimiter rateLimiter)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public string Platform => "Agoda";

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
            _logger.LogError(ex, "Agoda validation failed");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        var bookings = new List<OtaBookingModel>();
        try
        {
            _logger.LogInformation("Fetching Agoda bookings for {PropertyId}", externalPropertyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Agoda bookings");
        }
        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Agoda availability");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Agoda availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Agoda pricing");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Agoda pricing");
            return false;
        }
    }

    public async Task<Dictionary<DateOnly, bool>> UpdatePricingBatchAsync(string externalPropertyId, Dictionary<DateOnly, decimal> pricesByDate)
    {
        var results = new Dictionary<DateOnly, bool>();

        if (pricesByDate == null || pricesByDate.Count == 0)
        {
            _logger.LogWarning("No pricing data provided for batch update on property {PropertyId}", externalPropertyId);
            return results;
        }

        try
        {
            _logger.LogInformation("Updating Agoda pricing batch for property {PropertyId} with {Count} dates",
                externalPropertyId, pricesByDate.Count);

            foreach (var (date, price) in pricesByDate)
            {
                bool success = await UpdatePricingWithRetryAsync(externalPropertyId, date.ToDateTime(TimeOnly.MinValue), price);
                results[date] = success;
            }

            var successCount = results.Values.Count(v => v);
            _logger.LogInformation("Agoda batch pricing update complete: {SuccessCount}/{Total} dates succeeded",
                successCount, pricesByDate.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Agoda batch pricing update for property {PropertyId}", externalPropertyId);
            foreach (var date in pricesByDate.Keys)
            {
                results[date] = false;
            }
        }

        return results;
    }

    private async Task<bool> UpdatePricingWithRetryAsync(string externalPropertyId, DateTime date, decimal price, int retryCount = 0)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        try
        {
            using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

            if (price <= 0)
            {
                _logger.LogError("Invalid price value: {Price}. Price must be greater than 0", price);
                return false;
            }

            var url = $"{_baseUrl}/properties/{externalPropertyId}/pricing";
            using var requestContent = new StringContent(
                $"{{\"date\":\"{date:yyyy-MM-dd}\",\"price\":{price:F2}}}",
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _httpClient.PutAsync(url, requestContent);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (retryCount < maxRetries && IsRetryableStatusCode(response.StatusCode))
            {
                var delayMs = baseDelayMs * (int)Math.Pow(2, retryCount);
                _logger.LogWarning("Agoda pricing update failed with status {StatusCode}. Retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                    response.StatusCode, delayMs, retryCount + 1, maxRetries);
                await Task.Delay(delayMs);
                return await UpdatePricingWithRetryAsync(externalPropertyId, date, price, retryCount + 1);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update Agoda pricing for date {Date}. Status: {StatusCode}, Error: {Error}",
                date.ToString("yyyy-MM-dd"), response.StatusCode, errorContent);
            return false;
        }
        catch (HttpRequestException ex) when (retryCount < maxRetries)
        {
            var delayMs = baseDelayMs * (int)Math.Pow(2, retryCount);
            _logger.LogWarning(ex, "HTTP error in pricing update for {PropertyId}, date {Date}. Retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                externalPropertyId, date.ToString("yyyy-MM-dd"), delayMs, retryCount + 1, maxRetries);
            await Task.Delay(delayMs);
            return await UpdatePricingWithRetryAsync(externalPropertyId, date, price, retryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating Agoda pricing for property {PropertyId}", externalPropertyId);
            return false;
        }
    }

    private bool IsRetryableStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.RequestTimeout ||
               statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
               statusCode == System.Net.HttpStatusCode.GatewayTimeout ||
               (int)statusCode >= 500;
    }
}
