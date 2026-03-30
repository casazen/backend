## User Story

As a **property owner**, I want **full Airbnb integration**, so that **my property listings, availability, and bookings automatically sync with Airbnb**.

## Context

AirbnbAdapter exists but has TODO comments for actual API calls. Need to implement real Airbnb API integration.

## Technical Details

### Airbnb API Overview

- **Base URL**: `https://api.airbnb.com/v2/`
- **Authentication**: OAuth 2.0 bearer token
- **Rate Limit**: 5 requests/second
- **API Documentation**: https://airbnb.com/partner/api-docs

### Files to Modify

1. **Casazen.Infrastructure/OTA/AirbnbAdapter.cs** (complete implementation)

```csharp
public class AirbnbAdapter : IChannelAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirbnbAdapter> _logger;
    private const string BaseUrl = "https://api.airbnb.com/v2";

    public AirbnbAdapter(HttpClient httpClient, ILogger<AirbnbAdapter> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> SyncPropertyAsync(Guid propertyId, string externalId, string apiKey)
    {
        _logger.LogInformation("Syncing property {PropertyId} to Airbnb listing {ExternalId}",
            propertyId, externalId);

        try
        {
            var property = await GetPropertyAsync(propertyId);

            var requestBody = new
            {
                listing_id = externalId,
                name = property.Name,
                description = property.Description,
                bedrooms = property.Bedrooms,
                bathrooms = property.Bathrooms,
                max_guests = property.MaxGuests,
                nightly_price = property.NightlyRate,
                address = new
                {
                    street = property.Address,
                    city = property.City,
                    postal_code = property.PostalCode,
                    country = "IT"
                },
                amenities = property.Amenities,
                photos = property.PhotoUrls
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.PutAsJsonAsync(
                $"{BaseUrl}/listings/{externalId}",
                requestBody
            );

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully synced property {PropertyId} to Airbnb", propertyId);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to sync property to Airbnb: {StatusCode} - {Error}",
                response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing property {PropertyId} to Airbnb", propertyId);
            throw;
        }
    }

    public async Task<bool> UpdateAvailabilityAsync(Guid propertyId, DateTime startDate, DateTime endDate, bool available)
    {
        _logger.LogInformation("Updating Airbnb availability for property {PropertyId}: {StartDate} to {EndDate} = {Available}",
            propertyId, startDate, endDate, available);

        try
        {
            var integration = await GetOtaIntegrationAsync(propertyId, "Airbnb");

            var requestBody = new
            {
                listing_id = integration.ExternalPropertyId,
                start_date = startDate.ToString("yyyy-MM-dd"),
                end_date = endDate.ToString("yyyy-MM-dd"),
                available = available
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", integration.ApiKey);

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/calendars/{integration.ExternalPropertyId}/availability",
                requestBody
            );

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Airbnb availability for property {PropertyId}", propertyId);
            throw;
        }
    }

    public async Task<bool> UpdatePricingAsync(Guid propertyId, decimal newPrice)
    {
        _logger.LogInformation("Updating Airbnb pricing for property {PropertyId}: {NewPrice}",
            propertyId, newPrice);

        try
        {
            var integration = await GetOtaIntegrationAsync(propertyId, "Airbnb");

            var requestBody = new
            {
                listing_id = integration.ExternalPropertyId,
                nightly_price = newPrice
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", integration.ApiKey);

            var response = await _httpClient.PatchAsJsonAsync(
                $"{BaseUrl}/listings/{integration.ExternalPropertyId}/pricing",
                requestBody
            );

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Airbnb pricing for property {PropertyId}", propertyId);
            throw;
        }
    }

    public async Task<IEnumerable<Booking>> GetBookingsAsync(Guid propertyId)
    {
        _logger.LogInformation("Pulling bookings from Airbnb for property {PropertyId}", propertyId);

        try
        {
            var integration = await GetOtaIntegrationAsync(propertyId, "Airbnb");

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", integration.ApiKey);

            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/listings/{integration.ExternalPropertyId}/reservations?status=accepted"
            );

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get Airbnb bookings: {StatusCode}", response.StatusCode);
                return Enumerable.Empty<Booking>();
            }

            var airbnbReservations = await response.Content.ReadFromJsonAsync<AirbnbReservationsResponse>();

            return airbnbReservations.Reservations.Select(r => new Booking
            {
                PropertyId = propertyId,
                ExternalId = r.ConfirmationCode,
                Source = BookingSource.Airbnb,
                CheckInDate = DateTime.Parse(r.CheckInDate),
                CheckOutDate = DateTime.Parse(r.CheckOutDate),
                NumberOfGuests = r.GuestCount,
                TotalPrice = r.TotalPrice,
                Status = MapAirbnbStatus(r.Status),
                Guest = new Guest
                {
                    FirstName = r.Guest.FirstName,
                    LastName = r.Guest.LastName,
                    Email = r.Guest.Email,
                    PhoneNumber = r.Guest.Phone
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Airbnb bookings for property {PropertyId}", propertyId);
            throw;
        }
    }

    private BookingStatus MapAirbnbStatus(string airbnbStatus)
    {
        return airbnbStatus.ToLower() switch
        {
            "accepted" => BookingStatus.Confirmed,
            "pending" => BookingStatus.Pending,
            "cancelled" => BookingStatus.Cancelled,
            _ => BookingStatus.Pending
        };
    }
}

// Airbnb API response models
public class AirbnbReservationsResponse
{
    public List<AirbnbReservation> Reservations { get; set; }
}

public class AirbnbReservation
{
    public string ConfirmationCode { get; set; }
    public string CheckInDate { get; set; }
    public string CheckOutDate { get; set; }
    public int GuestCount { get; set; }
    public decimal TotalPrice { get; set; }
    public string Status { get; set; }
    public AirbnbGuest Guest { get; set; }
}

public class AirbnbGuest
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string Phone { get; set; }
}
```

2. **Configure HttpClient in Program.cs**
```csharp
builder.Services.AddHttpClient<AirbnbAdapter>(client =>
{
    client.BaseAddress = new Uri("https://api.airbnb.com/v2/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

3. **Integration Tests**
```csharp
// Casazen.Tests/Integration/OTA/AirbnbAdapterTests.cs
public class AirbnbAdapterTests
{
    [Fact]
    public async Task SyncPropertyAsync_ValidProperty_ReturnsTrue()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.airbnb.com/v2/listings/*")
                .Respond("application/json", "{ \"success\": true }");

        var httpClient = mockHttp.ToHttpClient();
        var adapter = new AirbnbAdapter(httpClient, new NullLogger<AirbnbAdapter>());

        // Act
        var result = await adapter.SyncPropertyAsync(Guid.NewGuid(), "listing123", "fake-key");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetBookingsAsync_ValidResponse_ReturnsBookings()
    {
        // Arrange
        var mockResponse = @"{
            ""reservations"": [
                {
                    ""confirmation_code"": ""ABC123"",
                    ""check_in_date"": ""2026-04-01"",
                    ""check_out_date"": ""2026-04-05"",
                    ""guest_count"": 2,
                    ""total_price"": 500.00,
                    ""status"": ""accepted"",
                    ""guest"": {
                        ""first_name"": ""John"",
                        ""last_name"": ""Doe"",
                        ""email"": ""john@example.com"",
                        ""phone"": ""+1234567890""
                    }
                }
            ]
        }";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("*/reservations*")
                .Respond("application/json", mockResponse);

        var httpClient = mockHttp.ToHttpClient();
        var adapter = new AirbnbAdapter(httpClient, new NullLogger<AirbnbAdapter>());

        // Act
        var bookings = await adapter.GetBookingsAsync(Guid.NewGuid());

        // Assert
        Assert.Single(bookings);
        var booking = bookings.First();
        Assert.Equal("ABC123", booking.ExternalId);
        Assert.Equal(BookingSource.Airbnb, booking.Source);
    }
}
```

## Acceptance Criteria

- [ ] AirbnbAdapter fully implemented with real API calls
- [ ] SyncPropertyAsync updates Airbnb listing
- [ ] UpdateAvailabilityAsync blocks/unblocks dates
- [ ] UpdatePricingAsync changes nightly rate
- [ ] GetBookingsAsync pulls reservations
- [ ] OAuth bearer token authentication
- [ ] Error handling for API failures
- [ ] Unit tests with mocked HttpClient
- [ ] Integration tests with Airbnb sandbox/test account
- [ ] Logging for all operations

## Testing

### Manual Testing with Airbnb Sandbox
1. Register for Airbnb Partner API access
2. Get sandbox API credentials
3. Create test listing
4. Run adapter methods against sandbox
5. Verify changes in Airbnb dashboard

## Definition of Done

- [ ] AirbnbAdapter fully implemented
- [ ] HttpClient configured
- [ ] Unit tests pass (mocked HTTP)
- [ ] Integration tests pass (sandbox)
- [ ] README updated with Airbnb API setup instructions
- [ ] Code reviewed

## Estimated Effort

**2-3 days**

## Priority

⚠️ **HIGH** - Core OTA integration

## Dependencies

- Issue #10 (Polly Resilience) - should be added after implementation

## Notes

- Airbnb API requires partnership approval (may take 2-4 weeks)
- Alternative: Use unofficial Airbnb API or scraping (not recommended for production)
- Consider using existing library: `AirbnbSharp` (if available)
