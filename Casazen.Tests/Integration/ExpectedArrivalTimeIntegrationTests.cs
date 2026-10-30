using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-03b over HTTP: the expected arrival time the guest gives at the public checkout is a note for the host. A time is kept as
/// <c>HH:mm</c>, <c>late</c> is kept, and anything else is refused with a stable code before anything is written.
/// </summary>
public class ExpectedArrivalTimeIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";

    private readonly CasazenWebApplicationFactory _factory;

    public ExpectedArrivalTimeIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task Checkout_ExpectedArrivalTime_IsKeptAsTheHostReadsIt_AndANonTimeIsRefusedBeforeAnythingIsWritten()
    {
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateClient();
        var monday = FarMonday();

        // " 9:30 " is a time (kept as 09:30), "LATE" is the late arrival, nothing at all is fine.
        var time = await client.PostAsJsonAsync("/api/public/bookings", Payload(propertyId, monday, " 9:30 "));
        var late = await client.PostAsJsonAsync("/api/public/bookings", Payload(propertyId, monday.AddDays(7), "LATE"));
        var none = await client.PostAsJsonAsync("/api/public/bookings", Payload(propertyId, monday.AddDays(14), null));

        Assert.Equal("09:30", await StoredArrivalTimeAsync(await BookingIdAsync(time)));
        Assert.Equal("late", await StoredArrivalTimeAsync(await BookingIdAsync(late)));
        Assert.Null(await StoredArrivalTimeAsync(await BookingIdAsync(none)));

        // Not a time: 422 with the stable code, and no booking is made for those dates.
        var refused = await client.PostAsJsonAsync("/api/public/bookings", Payload(propertyId, monday.AddDays(21), "25:00"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "direct_booking_arrival_time_invalid",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.Bookings.IgnoreQueryFilters().CountAsync(b => b.PropertyId == propertyId));
    }

    private static async Task<Guid> BookingIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
    }

    private async Task<string?> StoredArrivalTimeAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => b.ExpectedArrivalTime)
            .SingleAsync();
    }

    /// <summary>A Monday far enough ahead for any rule of the stay (midnight UTC of its Rome date).</summary>
    private static DateTime FarMonday()
    {
        var day = TimeProvider.System.TodayInRome().AddDays(60);
        while (day.DayOfWeek != DayOfWeek.Monday)
            day = day.AddDays(1);

        return day;
    }

    private static object Payload(Guid propertyId, DateTime checkIn, string? expectedArrivalTime) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkIn.AddDays(2).ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Mario",
            lastName = "Rossi",
            email = $"mario.{Guid.NewGuid():N}@example.com",
            phone = "+393331234567",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = "Immediate",
        expectedArrivalTime,
    };

    /// <summary>An org with a ready Stripe Connect account and a property published in Seveso (no tourist tax rate).</summary>
    private async Task<Guid> SeedPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "DB-03b Org",
            Slug = $"db03b-arrival-{Guid.NewGuid():N}",
            DisplayName = "DB-03b Org",
            ContactEmail = "db03b@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
            StripeConnectedAccountId = "acct_test_connect_ready",
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Casa DB-03b",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Latitude = 45.64m,
            Longitude = 9.13m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }
}
