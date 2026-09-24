using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-05 (A3-09, A2-13, R-03, A9-39) on real PostgreSQL: the public availability of the booking site shows the nights
/// taken by bookings, holds within their time, "pay at the property" requests waiting for the host and iCal / manual
/// calendar blocks, leaves out expired holds, answers 404 for a property that does not exist or is not published, and
/// agrees night by night with the public checkout (taken night → 409, free night → booking created).
/// </summary>
public class PublicAvailabilityPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const string AirbnbSummary = "Airbnb (Not available)";

    private readonly CasazenWebApplicationFactory _factory;

    public PublicAvailabilityPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetPropertyAvailability_ICalAndManualBlocks_NightsAreBookedWithoutDetails()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-blocks");
        var from = NextYear(10, 1);
        await SeedBlockAsync(property, from.AddDays(2), from.AddDays(5), CalendarBlockSource.ICalImport);
        await SeedBlockAsync(property, from.AddDays(8), from.AddDays(9), CalendarBlockSource.Manual);

        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(AvailabilityPath(property.Id, from, from.AddDays(14)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var booked = BookedDates(body);
        Assert.Equal([Day(from, 2), Day(from, 3), Day(from, 4), Day(from, 8)], booked);
        // Dates only: no summary of the feed, no source, no guest.
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["propertyId", "startDate", "endDate", "bookedDates"],
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("Airbnb", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manual", body, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_ExpiredHold_NightsAreFreeAndHoldWithinTtlIsBooked()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-holds");
        var from = NextYear(10, 1);
        // Test TTL: DirectBooking:PendingTtlMinutes = 15.
        var expired = await SeedBookingAsync(property, from, from.AddDays(3), minutesAgo: 30, NewPaymentIntentId());
        await SeedBookingAsync(property, from.AddDays(5), from.AddDays(6), minutesAgo: 5, NewPaymentIntentId());

        var booked = await BookedDatesAsync(property.Id, from, from.AddDays(10));

        Assert.Equal([Day(from, 5)], booked);
        // A read never cancels anything: the expiry is the job's (or the next checkout's) work.
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(expired)).Status);
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_PendingOnSiteRequest_NightsAreBooked()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-onsite");
        var from = NextYear(10, 1);
        await SeedBookingAsync(property, from, from.AddDays(2), minutesAgo: 600, paymentIntentId: null, b => AsOnSiteRequest(b, 60));
        await SeedBookingAsync(property, from.AddDays(4), from.AddDays(5), minutesAgo: 600, paymentIntentId: null,
            b => AsOnSiteRequest(b, -5));

        var booked = await BookedDatesAsync(property.Id, from, from.AddDays(10));

        // The request waiting for the host holds its nights; the one past its deadline does not.
        Assert.Equal([Day(from, 0), Day(from, 1)], booked);
    }

    [PostgresTheory]
    [InlineData(false, PropertyComplianceStatus.Active)]
    [InlineData(true, PropertyComplianceStatus.Pending)]
    [InlineData(true, PropertyComplianceStatus.Suspended)]
    public async Task GetPropertyAvailability_UnpublishedProperty_Returns404WithStableCode(
        bool isActive,
        PropertyComplianceStatus complianceStatus)
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-unpublished");
        await SeedBlockAsync(property, NextYear(10, 2), NextYear(10, 4), CalendarBlockSource.ICalImport);
        await WithDbAsync(db => db.Properties.Where(p => p.Id == property.Id).ExecuteUpdateAsync(set => set
            .SetProperty(p => p.IsActive, isActive)
            .SetProperty(p => p.ComplianceStatus, complianceStatus)));

        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(AvailabilityPath(property.Id, NextYear(10, 1), NextYear(10, 10)));

        await AssertPropertyNotFoundAsync(response);
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_UnknownProperty_Returns404WithStableCode()
    {
        using var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT");

        var response = await anonymous.GetAsync($"/api/public/bookings/property/{Guid.NewGuid()}/availability");

        var problem = await AssertPropertyNotFoundAsync(response);
        Assert.Equal("Questo alloggio non è disponibile per la prenotazione online.", problem.GetProperty("detail").GetString());
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_SlugInsteadOfId_Returns404()
    {
        // R-03: the route takes the property id only (the page passes property.id, never the slug of its URL).
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/public/bookings/property/villa-mare/availability");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_RangeLongerThanAYear_Returns422WithStableCode()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-range");
        using var anonymous = _factory.CreateClient();

        var tooLong = await anonymous.GetAsync(AvailabilityPath(property.Id, NextYear(1, 1), NextYear(1, 1).AddDays(400)));
        var empty = await anonymous.GetAsync(AvailabilityPath(property.Id, NextYear(1, 5), NextYear(1, 5)));

        foreach (var response in new[] { tooLong, empty })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("availability_range_invalid", problem.GetProperty("code").GetString());
        }
    }

    [PostgresFact]
    public async Task GetPropertyAvailability_EveryNight_AgreesWithPublicCheckout()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-coherence");
        var from = NextYear(11, 2);
        var to = from.AddDays(16);
        await SeedBookingAsync(property, from.AddDays(1), from.AddDays(3), minutesAgo: 600, paymentIntentId: null, b =>
        {
            b.Status = BookingStatus.Confirmed;
            b.Source = BookingSource.Manual;
        });
        await SeedBookingAsync(property, from.AddDays(4), from.AddDays(5), minutesAgo: 5, NewPaymentIntentId());
        await SeedBookingAsync(property, from.AddDays(6), from.AddDays(7), minutesAgo: 30, NewPaymentIntentId());
        await SeedBookingAsync(property, from.AddDays(8), from.AddDays(9), minutesAgo: 600, paymentIntentId: null,
            b => AsOnSiteRequest(b, 60));
        await SeedBlockAsync(property, from.AddDays(10), from.AddDays(12), CalendarBlockSource.ICalImport);
        await SeedBlockAsync(property, from.AddDays(13), from.AddDays(14), CalendarBlockSource.Manual);
        await SeedBookingAsync(property, from.AddDays(15), from.AddDays(16), minutesAgo: 600, paymentIntentId: null,
            b => b.Status = BookingStatus.Cancelled);

        var booked = await BookedDatesAsync(property.Id, from, to);
        Assert.Equal(
            [Day(from, 1), Day(from, 2), Day(from, 4), Day(from, 8), Day(from, 10), Day(from, 11), Day(from, 13)],
            booked);

        using var guest = _factory.CreateClient();
        for (var night = from; night < to; night = night.AddDays(1))
        {
            var response = await guest.PostAsJsonAsync(
                "/api/public/bookings", CheckoutPayload(property.Id, night, night.AddDays(1)));

            var expected = booked.Contains(night.ToString("yyyy-MM-dd")) ? HttpStatusCode.Conflict : HttpStatusCode.OK;
            Assert.True(
                expected == response.StatusCode,
                $"Night {night:yyyy-MM-dd}: expected {expected}, got {response.StatusCode}");
            if (expected == HttpStatusCode.Conflict)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("booking_dates_unavailable", problem.GetProperty("code").GetString());
            }
        }

        // Every night that was free is now held by the checkout just created: the availability shows it at once.
        var after = await BookedDatesAsync(property.Id, from, to);
        Assert.Equal(Enumerable.Range(0, 16).Select(offset => Day(from, offset)).ToList(), after);
    }

    [PostgresFact]
    public async Task GetBookingStatus_LegacyAnonymousEndpoint_IsGone()
    {
        // A9-39: GET /{bookingId}/status answered for any booking id without a token; the outcome page does not use it.
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-status");
        var bookingId = await SeedBookingAsync(
            property, NextYear(10, 1), NextYear(10, 3), minutesAgo: 1, NewPaymentIntentId());
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/public/bookings/{bookingId}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    internal static string AvailabilityPath(Guid propertyId, DateTime from, DateTime to) =>
        $"/api/public/bookings/property/{propertyId}/availability?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}";

    internal static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static string Day(DateTime from, int offset) => from.AddDays(offset).ToString("yyyy-MM-dd");

    private static List<string> BookedDates(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    private async Task<List<string>> BookedDatesAsync(Guid propertyId, DateTime from, DateTime to)
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(AvailabilityPath(propertyId, from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return BookedDates(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> AssertPropertyNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("public_property_not_found", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("bookedDates", out _));
        return problem;
    }

    private static string NewPaymentIntentId() => $"pi_bk05_{Guid.NewGuid():N}";

    /// <summary>A "pay at the property" request whose deadline is <paramref name="minutesLeft"/> minutes away.</summary>
    private static void AsOnSiteRequest(Booking booking, int minutesLeft)
    {
        booking.PaymentOption = PaymentOption.OnSite;
        booking.GuestEmailVerifiedAt = DateTime.UtcNow.AddMinutes(-590);
        booking.RequestExpiresAt = DateTime.UtcNow.AddMinutes(minutesLeft);
    }

    private static object CheckoutPayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Giulia",
            lastName = "Bianchi",
            email = $"giulia.{Guid.NewGuid():N}@example.com",
            phone = "+393339876543",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = nameof(PaymentOption.Immediate),
    };

    /// <summary>
    /// A booking of the public checkout (Pending, Direct, Immediate) created <paramref name="minutesAgo"/> minutes ago,
    /// with its PaymentIntent when given; <paramref name="configure"/> turns it into another kind of stay.
    /// </summary>
    private async Task<Guid> SeedBookingAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        int minutesAgo,
        string? paymentIntentId,
        Action<Booking>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            TotalPrice = 200m,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        configure?.Invoke(booking);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        if (paymentIntentId is not null)
        {
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = property.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Pending,
                StripePaymentIntentId = paymentIntentId,
                TransactionId = paymentIntentId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync();
        return booking.Id;
    }

    private Task SeedBlockAsync(Property property, DateTime start, DateTime end, CalendarBlockSource source) =>
        WithDbAsync(db =>
        {
            db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Source = source,
                ExternalUid = source == CalendarBlockSource.ICalImport ? $"uid-{Guid.NewGuid():N}@airbnb.com" : null,
                StartUtc = start,
                EndUtc = end,
                Summary = source == CalendarBlockSource.ICalImport ? AirbnbSummary : "Manutenzione",
                LastSyncedAt = DateTime.UtcNow,
            });
            return db.SaveChangesAsync();
        });

    private async Task<Booking> LoadBookingAsync(Guid bookingId) =>
        await WithDbAsync(db => db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId));

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

/// <summary>
/// BK-05 (A9-39, FD-10): the public availability is rate limited per client IP. Two requests per window here: the third
/// from the same IP gets 429 <c>rate_limited</c> with <c>Retry-After</c>, another IP is still served.
/// </summary>
public class PublicAvailabilityRateLimitPostgresTests
    : IClassFixture<PublicAvailabilityRateLimitPostgresTests.LowPublicReadLimitFactory>
{
    private readonly LowPublicReadLimitFactory _factory;

    public PublicAvailabilityRateLimitPostgresTests(LowPublicReadLimitFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetPropertyAvailability_TooManyRequestsFromOneIp_Returns429()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "bk05-ratelimit");
        var path = PublicAvailabilityPostgresTests.AvailabilityPath(
            property.Id, PublicAvailabilityPostgresTests.NextYear(10, 1), PublicAvailabilityPostgresTests.NextYear(10, 31));
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, path, "203.0.113.51")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, path, "203.0.113.51")).StatusCode);
        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(await SendAsync(client, path, "203.0.113.51"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, path, "198.51.100.51")).StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, clientIp);
        return client.SendAsync(request);
    }

    /// <summary>The integration host with two anonymous catalogue reads per client IP and window.</summary>
    public sealed class LowPublicReadLimitFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PublicRead:PermitLimit"] = "2",
                ["RateLimiting:PublicRead:WindowSeconds"] = "300",
            }));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>());
        }
    }
}

/// <summary>A property shown on the booking site (active, compliance activated) of an org ready to take payments.</summary>
internal static class PublishedPropertyTests
{
    public static async Task<Property> SeedAsync(CasazenWebApplicationFactory factory, string label)
    {
        var seeded = await factory.SeedPropertyAsync($"auth0|{label}-{Guid.NewGuid():N}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = $"acct_bk05_{Guid.NewGuid():N}";
        org.ConnectChargesEnabled = true;
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return property;
    }
}
