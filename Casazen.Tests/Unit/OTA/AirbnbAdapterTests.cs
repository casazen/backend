using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Microsoft.Extensions.Logging;
using Moq;
using RichardSzalay.MockHttp;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Unit.OTA;

public class AirbnbAdapterTests
{
    private readonly MockHttpMessageHandler _mockHttp;
    private readonly Mock<ILogger<AirbnbAdapter>> _mockLogger;
    private readonly Mock<OtaRateLimiter> _mockRateLimiter;
    private readonly AirbnbAdapter _adapter;
    private const string BaseUrl = "https://api.airbnb.com/v2";
    private const string TestApiKey = "test_airbnb_api_key_123";
    private const string TestPropertyId = "airbnb_listing_456";

    public AirbnbAdapterTests()
    {
        _mockHttp = new MockHttpMessageHandler();
        _mockLogger = new Mock<ILogger<AirbnbAdapter>>();
        _mockRateLimiter = new Mock<OtaRateLimiter>();
        _mockRateLimiter.Setup(x => x.AcquireAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(new OtaRateLimiter.RateLimitToken(It.IsAny<SemaphoreSlim>(), It.IsAny<string>(), It.IsAny<ILogger>()));
        var httpClient = _mockHttp.ToHttpClient();
        _adapter = new AirbnbAdapter(httpClient, _mockLogger.Object, _mockRateLimiter.Object);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public void Platform_ReturnsAirbnb()
    {
        var platform = _adapter.Platform;

        Assert.Equal("Airbnb", platform);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task ValidateCredentialsAsync_WithValidApiKey_ReturnsTrue()
    {
        _mockHttp.When($"{BaseUrl}/listings")
            .WithHeaders("Authorization", $"Bearer {TestApiKey}")
            .Respond(HttpStatusCode.OK, "application/json", "{}");

        var result = await _adapter.ValidateCredentialsAsync(TestApiKey);

        Assert.True(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task ValidateCredentialsAsync_WithInvalidApiKey_ReturnsFalse()
    {
        _mockHttp.When($"{BaseUrl}/listings")
            .Respond(HttpStatusCode.Unauthorized);

        var result = await _adapter.ValidateCredentialsAsync("invalid_key");

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task ValidateCredentialsAsync_WithNetworkError_ReturnsFalse()
    {
        _mockHttp.When($"{BaseUrl}/listings")
            .Throw(new HttpRequestException("Network error"));

        var result = await _adapter.ValidateCredentialsAsync(TestApiKey);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task GetBookingsAsync_WithValidResponse_ReturnsBookings()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        var mockResponse = new AirbnbReservationsResponse
        {
            Reservations = new List<AirbnbReservation>
            {
                new()
                {
                    ConfirmationCode = "AIRBNB123",
                    Status = "confirmed",
                    CheckIn = new DateTime(2026, 4, 5),
                    CheckOut = new DateTime(2026, 4, 10),
                    TotalPrice = 500.00m,
                    Currency = "EUR",
                    Guest = new AirbnbGuest
                    {
                        FirstName = "Mario",
                        LastName = "Rossi",
                        Email = "mario.rossi@example.com",
                        Phone = "+39 123 456 7890"
                    }
                },
                new()
                {
                    ConfirmationCode = "AIRBNB456",
                    Status = "pending",
                    CheckIn = new DateTime(2026, 4, 15),
                    CheckOut = new DateTime(2026, 4, 20),
                    TotalPrice = 600.00m,
                    Currency = "EUR",
                    Guest = new AirbnbGuest
                    {
                        FirstName = "Lucia",
                        LastName = "Bianchi",
                        Email = "lucia.bianchi@example.com",
                        Phone = "+39 987 654 3210"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(mockResponse);
        _mockHttp.When($"{BaseUrl}/listings/{TestPropertyId}/reservations")
            .WithQueryString($"start_date={startDate:yyyy-MM-dd}&end_date={endDate:yyyy-MM-dd}")
            .Respond("application/json", jsonResponse);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.NotNull(bookings);
        Assert.Equal(2, bookings.Count);

        var firstBooking = bookings[0];
        Assert.Equal("AIRBNB123", firstBooking.ExternalBookingId);
        Assert.Equal("Mario Rossi", firstBooking.GuestName);
        Assert.Equal("mario.rossi@example.com", firstBooking.GuestEmail);
        Assert.Equal(500.00m, firstBooking.TotalPrice);
        Assert.Equal("Confirmed", firstBooking.Status);
        Assert.Equal(new DateTime(2026, 4, 5), firstBooking.CheckInDate);
        Assert.Equal(new DateTime(2026, 4, 10), firstBooking.CheckOutDate);

        var secondBooking = bookings[1];
        Assert.Equal("AIRBNB456", secondBooking.ExternalBookingId);
        Assert.Equal("Lucia Bianchi", secondBooking.GuestName);
        Assert.Equal("Pending", secondBooking.Status);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task GetBookingsAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        var mockResponse = new AirbnbReservationsResponse
        {
            Reservations = new List<AirbnbReservation>()
        };

        var jsonResponse = JsonSerializer.Serialize(mockResponse);
        _mockHttp.When($"{BaseUrl}/listings/{TestPropertyId}/reservations")
            .Respond("application/json", jsonResponse);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.NotNull(bookings);
        Assert.Empty(bookings);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task GetBookingsAsync_WithApiError_ReturnsEmptyList()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        _mockHttp.When($"{BaseUrl}/listings/{TestPropertyId}/reservations")
            .Respond(HttpStatusCode.InternalServerError);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.NotNull(bookings);
        Assert.Empty(bookings);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task GetBookingsAsync_WithoutApiKey_ReturnsEmptyList()
    {
        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.NotNull(bookings);
        Assert.Empty(bookings);
    }

    [Theory(Skip = "OTA partner API in freeze (D10)")]
    [InlineData("confirmed", "Confirmed")]
    [InlineData("pending", "Pending")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("declined", "Cancelled")]
    [InlineData("completed", "Completed")]
    [InlineData("unknown_status", "Unknown")]
    public async Task GetBookingsAsync_StatusMapping_MapsCorrectly(string airbnbStatus, string expectedStatus)
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        var mockResponse = new AirbnbReservationsResponse
        {
            Reservations = new List<AirbnbReservation>
            {
                new()
                {
                    ConfirmationCode = "TEST123",
                    Status = airbnbStatus,
                    CheckIn = new DateTime(2026, 4, 5),
                    CheckOut = new DateTime(2026, 4, 10),
                    TotalPrice = 500.00m,
                    Guest = new AirbnbGuest
                    {
                        FirstName = "Test",
                        LastName = "User",
                        Email = "test@example.com"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(mockResponse);
        _mockHttp.When($"{BaseUrl}/listings/{TestPropertyId}/reservations")
            .Respond("application/json", jsonResponse);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.Single(bookings);
        Assert.Equal(expectedStatus, bookings[0].Status);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdateAvailabilityAsync_WithValidRequest_ReturnsTrue()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);

        _mockHttp.When(HttpMethod.Put, $"{BaseUrl}/calendar/availability")
            .WithHeaders("Authorization", $"Bearer {TestApiKey}")
            .Respond(HttpStatusCode.OK, "application/json", "{\"success\":true}");

        var result = await _adapter.UpdateAvailabilityAsync(TestPropertyId, date, true);

        Assert.True(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdateAvailabilityAsync_WithApiError_ReturnsFalse()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);

        _mockHttp.When(HttpMethod.Put, $"{BaseUrl}/calendar/availability")
            .Respond(HttpStatusCode.BadRequest, "application/json", "{\"error\":\"Invalid request\"}");

        var result = await _adapter.UpdateAvailabilityAsync(TestPropertyId, date, false);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdateAvailabilityAsync_WithoutApiKey_ReturnsFalse()
    {
        var date = new DateTime(2026, 4, 15);

        var result = await _adapter.UpdateAvailabilityAsync(TestPropertyId, date, true);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithValidRequest_ReturnsTrue()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);
        var price = 150.00m;

        _mockHttp.When(HttpMethod.Put, $"{BaseUrl}/calendar/pricing")
            .WithHeaders("Authorization", $"Bearer {TestApiKey}")
            .Respond(HttpStatusCode.OK, "application/json", "{\"success\":true}");

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, price);

        Assert.True(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithInvalidPrice_ReturnsFalse()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);
        var invalidPrice = 0m;

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, invalidPrice);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithNegativePrice_ReturnsFalse()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);
        var negativePrice = -50m;

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, negativePrice);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithApiError_ReturnsFalse()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);
        var price = 150.00m;

        _mockHttp.When(HttpMethod.Put, $"{BaseUrl}/calendar/pricing")
            .Respond(HttpStatusCode.InternalServerError);

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, price);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithoutApiKey_ReturnsFalse()
    {
        var date = new DateTime(2026, 4, 15);
        var price = 150.00m;

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, price);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task UpdatePricingAsync_WithNetworkError_ReturnsFalse()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var date = new DateTime(2026, 4, 15);
        var price = 150.00m;

        _mockHttp.When(HttpMethod.Put, $"{BaseUrl}/calendar/pricing")
            .Throw(new HttpRequestException("Network timeout"));

        var result = await _adapter.UpdatePricingAsync(TestPropertyId, date, price);

        Assert.False(result);
    }

    [Fact(Skip = "OTA partner API in freeze (D10)")]
    public async Task GetBookingsAsync_WithGuestNameVariations_HandlesCorrectly()
    {
        await _adapter.ValidateCredentialsAsync(TestApiKey);

        var startDate = new DateTime(2026, 4, 1);
        var endDate = new DateTime(2026, 4, 30);

        var mockResponse = new AirbnbReservationsResponse
        {
            Reservations = new List<AirbnbReservation>
            {
                new()
                {
                    ConfirmationCode = "TEST1",
                    Status = "confirmed",
                    CheckIn = startDate,
                    CheckOut = endDate,
                    TotalPrice = 100m,
                    Guest = new AirbnbGuest
                    {
                        FirstName = "John",
                        LastName = "Doe",
                        Email = "john@example.com"
                    }
                },
                new()
                {
                    ConfirmationCode = "TEST2",
                    Status = "confirmed",
                    CheckIn = startDate,
                    CheckOut = endDate,
                    TotalPrice = 100m,
                    Guest = new AirbnbGuest
                    {
                        FirstName = "Jane",
                        LastName = "",
                        Email = "jane@example.com"
                    }
                },
                new()
                {
                    ConfirmationCode = "TEST3",
                    Status = "confirmed",
                    CheckIn = startDate,
                    CheckOut = endDate,
                    TotalPrice = 100m,
                    Guest = new AirbnbGuest
                    {
                        FirstName = "",
                        LastName = "Smith",
                        Email = "smith@example.com"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(mockResponse);
        _mockHttp.When($"{BaseUrl}/listings/{TestPropertyId}/reservations")
            .Respond("application/json", jsonResponse);

        var bookings = await _adapter.GetBookingsAsync(TestPropertyId, startDate, endDate);

        Assert.Equal(3, bookings.Count);
        Assert.Equal("John Doe", bookings[0].GuestName);
        Assert.Equal("Jane", bookings[1].GuestName);
        Assert.Equal("Smith", bookings[2].GuestName);
    }
}
