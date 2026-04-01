using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.OTA.Resilience;

namespace Casazen.Infrastructure.OTA;

public class AirbnbAdapter : IChannelAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirbnbAdapter> _logger;
    private readonly OtaRateLimiter _rateLimiter;
    private readonly string _baseUrl = "https://api.airbnb.com/v2";
    private string? _apiKey;

    public AirbnbAdapter(HttpClient httpClient, ILogger<AirbnbAdapter> logger, OtaRateLimiter rateLimiter)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public string Platform => "Airbnb";

    /// <summary>
    /// Sets the authentication header for Airbnb API requests
    /// </summary>
    private void SetAuthenticationHeader(string apiKey)
    {
        _apiKey = apiKey;
        if (!_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }
    }

    public async Task<bool> ValidateCredentialsAsync(string apiKey, string? apiSecret = null)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Validating Airbnb API credentials");

            SetAuthenticationHeader(apiKey);

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/listings");
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Airbnb API credentials validated successfully");
                return true;
            }

            _logger.LogWarning("Airbnb API credentials validation failed with status code: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Airbnb validation failed with exception");
            return false;
        }
    }

    public async Task<List<OtaBookingModel>> GetBookingsAsync(string externalPropertyId, DateTime startDate, DateTime endDate)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        var bookings = new List<OtaBookingModel>();
        try
        {
            _logger.LogInformation("Fetching Airbnb bookings for property {PropertyId} from {StartDate} to {EndDate}",
                externalPropertyId, startDate, endDate);

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogError("API key not set. Call ValidateCredentialsAsync first");
                return bookings;
            }

            var url = $"{_baseUrl}/listings/{externalPropertyId}/reservations?start_date={startDate:yyyy-MM-dd}&end_date={endDate:yyyy-MM-dd}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch Airbnb bookings. Status code: {StatusCode}", response.StatusCode);
                return bookings;
            }

            var apiResponse = await response.Content.ReadFromJsonAsync<AirbnbReservationsResponse>();

            if (apiResponse?.Reservations == null || !apiResponse.Reservations.Any())
            {
                _logger.LogInformation("No bookings found for property {PropertyId}", externalPropertyId);
                return bookings;
            }

            foreach (var reservation in apiResponse.Reservations)
            {
                bookings.Add(new OtaBookingModel
                {
                    ExternalBookingId = reservation.ConfirmationCode,
                    CheckInDate = reservation.CheckIn,
                    CheckOutDate = reservation.CheckOut,
                    GuestName = $"{reservation.Guest.FirstName} {reservation.Guest.LastName}".Trim(),
                    GuestEmail = reservation.Guest.Email,
                    TotalPrice = reservation.TotalPrice,
                    Status = MapAirbnbStatusToInternal(reservation.Status)
                });
            }

            _logger.LogInformation("Successfully fetched {Count} bookings from Airbnb", bookings.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error deserializing Airbnb API response");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching Airbnb bookings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching Airbnb bookings");
        }

        return bookings;
    }

    public async Task<bool> UpdateAvailabilityAsync(string externalPropertyId, DateTime date, bool isAvailable)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Airbnb availability for property {PropertyId} on {Date} to {Available}",
                externalPropertyId, date, isAvailable);

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogError("API key not set. Call ValidateCredentialsAsync first");
                return false;
            }

            var request = new AirbnbAvailabilityRequest
            {
                ListingId = externalPropertyId,
                Date = date,
                Available = isAvailable
            };

            var url = $"{_baseUrl}/calendar/availability";
            var response = await _httpClient.PutAsJsonAsync(url, request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to update Airbnb availability. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Successfully updated Airbnb availability for property {PropertyId}", externalPropertyId);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating Airbnb availability");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating Airbnb availability");
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(string externalPropertyId, DateTime date, decimal price)
    {
        using var rateLimitToken = await _rateLimiter.AcquireAsync(Platform);

        try
        {
            _logger.LogInformation("Updating Airbnb pricing for property {PropertyId} on {Date} to {Price} EUR",
                externalPropertyId, date, price);

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogError("API key not set. Call ValidateCredentialsAsync first");
                return false;
            }

            if (price <= 0)
            {
                _logger.LogError("Invalid price value: {Price}. Price must be greater than 0", price);
                return false;
            }

            var request = new AirbnbPricingRequest
            {
                ListingId = externalPropertyId,
                Date = date,
                Price = price,
                Currency = "EUR"
            };

            var url = $"{_baseUrl}/calendar/pricing";
            var response = await _httpClient.PutAsJsonAsync(url, request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to update Airbnb pricing. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Successfully updated Airbnb pricing for property {PropertyId}", externalPropertyId);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating Airbnb pricing");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating Airbnb pricing");
            return false;
        }
    }

    /// <summary>
    /// Maps Airbnb reservation status to internal status
    /// </summary>
    private string MapAirbnbStatusToInternal(string airbnbStatus)
    {
        return airbnbStatus.ToLowerInvariant() switch
        {
            "confirmed" => "Confirmed",
            "pending" => "Pending",
            "cancelled" => "Cancelled",
            "declined" => "Cancelled",
            "completed" => "Completed",
            _ => "Unknown"
        };
    }
}
