using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-01 (A2-01) on real PostgreSQL: a booking entered by the host is <c>Confirmed</c> with source <c>Manual</c>, keeps
/// its dates taken on the booking site and in the iCal export, and is never cancelled by the cleanup of abandoned
/// checkout holds that the public checkout runs; the cleanup only touches expired holds of the requested dates.
/// </summary>
public class ManualBookingPostgresIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const string HostRole = "PropertyOwner";
    private readonly CasazenWebApplicationFactory _factory;

    public ManualBookingPostgresIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [PostgresFact]
    public async Task CreateManualBooking_GuestOpensCheckoutThirtyMinutesLater_HostBookingStaysConfirmedAndDatesStayTaken()
    {
        // Audit scenario: at 10:00 the host records a phone booking for 1-5 October; at 10:30 a guest opens the public
        // checkout for the same dates, then for other dates. The host booking must survive both.
        var (hostId, property) = await SeedCheckoutReadyHostPropertyAsync();
        var october1 = new DateTime(RomeToday().Year + 1, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var october5 = october1.AddDays(4);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var create = await PostManualBookingAsync(host, property.Id, october1, october5);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var hostBookingId = created.GetProperty("id").GetGuid();
        Assert.Equal("Confirmed", created.GetProperty("status").GetString());
        Assert.Equal("Manual", created.GetProperty("source").GetString());

        await ShiftCreationBackAsync(hostBookingId, TimeSpan.FromMinutes(30));

        using var guest = _factory.CreateClient();
        var sameDates = await guest.PostAsJsonAsync("/api/public/bookings", CheckoutPayload(property.Id, october1, october5));
        Assert.Equal(HttpStatusCode.Conflict, sameDates.StatusCode);

        var otherDates = await guest.PostAsJsonAsync(
            "/api/public/bookings", CheckoutPayload(property.Id, october1.AddDays(9), october1.AddDays(13)));
        Assert.Equal(HttpStatusCode.OK, otherDates.StatusCode);

        var stored = await LoadBookingAsync(hostBookingId);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(BookingSource.Manual, stored.Source);
        Assert.NotNull(stored.CheckInToken);

        var availability = await guest.GetFromJsonAsync<JsonElement>(
            $"/api/public/bookings/property/{property.Id}/availability" +
            $"?startDate={october1:yyyy-MM-dd}&endDate={october1.AddDays(30):yyyy-MM-dd}");
        var bookedDates = availability.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()).ToList();
        for (var night = october1; night < october5; night = night.AddDays(1))
            Assert.Contains(night.ToString("yyyy-MM-dd"), bookedDates);
        Assert.DoesNotContain(october5.ToString("yyyy-MM-dd"), bookedDates);

        var exportToken = await SeedExportFeedAsync(property);
        var ics = await guest.GetStringAsync($"/api/public/ical/{exportToken}");
        Assert.Contains($"UID:booking-{hostBookingId}", ics);
    }

    [PostgresFact]
    public async Task CreateManualBooking_OverlappingHostBooking_Returns409WithStableCodeAndStoresNoGuest()
    {
        var (hostId, property) = await SeedCheckoutReadyHostPropertyAsync();
        var october1 = new DateTime(RomeToday().Year + 1, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var first = await PostManualBookingAsync(host, property.Id, october1, october1.AddDays(4));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var overlappingEmail = $"overlap.{Guid.NewGuid():N}@example.com";
        var overlapping = await PostManualBookingAsync(
            host, property.Id, october1.AddDays(2), october1.AddDays(6), overlappingEmail);

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
        var problem = await overlapping.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("booking_dates_unavailable", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Guests.CountAsync(g => g.Email == overlappingEmail));
        Assert.Equal(1, await db.Bookings.CountAsync(b =>
            b.PropertyId == property.Id && b.Status != BookingStatus.Cancelled));
    }

    [PostgresFact]
    public async Task PublicCheckout_ExpiredHoldOfOtherDates_IsLeftPendingAndOnlyTheOverlappingHoldIsCancelled()
    {
        var (_, property) = await SeedCheckoutReadyHostPropertyAsync();
        var october1 = new DateTime(RomeToday().Year + 1, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var abandonedHoldId = await SeedExpiredPaymentIntentHoldAsync(property, october1.AddDays(19), october1.AddDays(21));
        using var guest = _factory.CreateClient();

        var otherDates = await guest.PostAsJsonAsync(
            "/api/public/bookings", CheckoutPayload(property.Id, october1.AddDays(9), october1.AddDays(11)));

        Assert.Equal(HttpStatusCode.OK, otherDates.StatusCode);
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(abandonedHoldId)).Status);

        var holdDates = await guest.PostAsJsonAsync(
            "/api/public/bookings", CheckoutPayload(property.Id, october1.AddDays(19), october1.AddDays(21)));

        Assert.Equal(HttpStatusCode.OK, holdDates.StatusCode);
        Assert.Equal(BookingStatus.Cancelled, (await LoadBookingAsync(abandonedHoldId)).Status);
    }

    [PostgresFact]
    public async Task ManualBookingStartingToday_GuestCheckInLinkAndHostCheckIn_Succeed()
    {
        var (hostId, property) = await SeedCheckoutReadyHostPropertyAsync();
        var today = RomeToday();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var create = await PostManualBookingAsync(host, property.Id, today, today.AddDays(2));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var bookingId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var link = await host.PostAsync($"/api/bookings/{bookingId}/checkin/resend-link", null);

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var linkBody = await link.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(linkBody.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(linkBody.GetProperty("checkInLink").GetString()));

        var checkIn = await host.PostAsync($"/api/bookings/{bookingId}/check-in", null);

        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        Assert.Equal("CheckedIn", (await checkIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(BookingStatus.CheckedIn, (await LoadBookingAsync(bookingId)).Status);
    }

    private static DateTime RomeToday() => TimeProvider.System.TodayInRome();

    private static Task<HttpResponseMessage> PostManualBookingAsync(
        HttpClient client,
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        string? email = null) =>
        client.PostAsJsonAsync("/api/bookings", new
        {
            propertyId,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkOut.ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = email ?? $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "Italia",
            },
        });

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

    /// <summary>A host with an org ready for Stripe Connect and an active, bookable property it owns.</summary>
    private async Task<(string HostId, Property Property)> SeedCheckoutReadyHostPropertyAsync()
    {
        var hostId = $"auth0|manual-host-{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = "acct_test_connect_ready";
        org.ConnectChargesEnabled = true;
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return (hostId, property);
    }

    /// <summary>An abandoned public checkout: Pending direct booking with a PaymentIntent, created 30 minutes ago.</summary>
    private async Task<Guid> SeedExpiredPaymentIntentHoldAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var createdAt = DateTime.UtcNow.AddMinutes(-30);
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var hold = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            TotalPrice = 350m,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(hold);
        db.Payments.Add(new Payment
        {
            BookingId = hold.Id,
            OrgId = property.OrgId,
            Amount = hold.TotalPrice,
            Status = PaymentStatus.Pending,
            StripePaymentIntentId = $"pi_abandoned_{Guid.NewGuid():N}",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private async Task<Guid> SeedExportFeedAsync(Property property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ExportToken = Guid.NewGuid(),
        };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.ExportToken;
    }

    /// <summary>Moves the creation time back, as if <paramref name="elapsed"/> had passed since the booking was made.</summary>
    private async Task ShiftCreationBackAsync(Guid bookingId, TimeSpan elapsed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.CreatedAt -= elapsed;
        booking.UpdatedAt -= elapsed;
        await db.SaveChangesAsync();
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }
}
