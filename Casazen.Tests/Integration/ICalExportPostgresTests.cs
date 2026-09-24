using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-12 (A2-22) on PostgreSQL: the public iCal export publishes the stays of CasaZen (host bookings, confirmed and
/// still valid checkout holds) as all-day events with a neutral SUMMARY, never the blocks imported from an OTA (echo),
/// an expired hold or a pending "pay at the property" request; the host can regenerate the link (the old one answers
/// 404) and a host of another org cannot. Downloads go through the scripted client of
/// <see cref="PropertyICalSyncPostgresTests.Factory"/>: no network.
/// </summary>
public class ICalExportPostgresTests : IClassFixture<PropertyICalSyncPostgresTests.Factory>
{
    private const string HostRole = "PropertyOwner";

    private readonly PropertyICalSyncPostgresTests.Factory _factory;

    public ICalExportPostgresTests(PropertyICalSyncPostgresTests.Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task PublicExport_BlockImportedFromAirbnb_IsNotExportedBackAndTheGuestNameNeverAppears()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc12-echo");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var airbnbFrom = PublicAvailabilityPostgresTests.NextYear(10, 2);
        var airbnbUrl = $"https://www.airbnb.it/calendar/ical/{Guid.NewGuid():N}.ics?s=pc12";
        _factory.Feeds[airbnbUrl] = () =>
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Airbnb Inc//Hosting Calendar//EN\r\n"
            + $"BEGIN:VEVENT\r\nUID:{Guid.NewGuid():N}@airbnb.com\r\nDTSTART;VALUE=DATE:{airbnbFrom:yyyyMMdd}\r\n"
            + $"DTEND;VALUE=DATE:{airbnbFrom.AddDays(3):yyyyMMdd}\r\nSUMMARY:Mario Rossi (HMABCDEF12)\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        var added = await owner.PostAsJsonAsync($"/api/properties/{property.Id}/ical/feeds", new { importUrl = airbnbUrl });
        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        await SyncAsync((await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var hostBooking = await SeedBookingAsync(
            property, PublicAvailabilityPostgresTests.NextYear(10, 10), PublicAvailabilityPostgresTests.NextYear(10, 12),
            b =>
            {
                b.Status = BookingStatus.Confirmed;
                b.Source = BookingSource.Manual;
            });

        var ics = await PublicFeedAsync(await ExportPathAsync(owner, property.Id));

        // The block was imported (it takes its nights on CasaZen) but it is not sent back to the OTAs.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.PropertyId == property.Id && b.Source == CalendarBlockSource.ICalImport));
        }

        var lines = ics.Split("\r\n");
        Assert.Single(lines, l => l == "BEGIN:VEVENT");
        Assert.Contains($"UID:booking-{hostBooking}", lines);
        Assert.Contains($"DTSTART;VALUE=DATE:{PublicAvailabilityPostgresTests.NextYear(10, 10):yyyyMMdd}", lines);
        Assert.Contains($"DTEND;VALUE=DATE:{PublicAvailabilityPostgresTests.NextYear(10, 12):yyyyMMdd}", lines);
        Assert.Contains("SUMMARY:Occupato", lines);
        Assert.DoesNotContain($"{airbnbFrom:yyyyMMdd}", ics);
        Assert.DoesNotContain("Mario", ics);
        Assert.DoesNotContain("HMABCDEF12", ics);
        Assert.DoesNotContain("airbnb.com", ics);
    }

    [PostgresFact]
    public async Task PublicExport_ExpiredPendingHold_IsNotExportedWhileAValidHoldIs()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc12-hold");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var expired = await SeedBookingAsync(
            property, PublicAvailabilityPostgresTests.NextYear(11, 1), PublicAvailabilityPostgresTests.NextYear(11, 4),
            b => b.CreatedAt = DateTime.UtcNow.AddHours(-2), paymentIntentId: $"pi_pc12_{Guid.NewGuid():N}");
        var valid = await SeedBookingAsync(
            property, PublicAvailabilityPostgresTests.NextYear(11, 10), PublicAvailabilityPostgresTests.NextYear(11, 12),
            b => b.CreatedAt = DateTime.UtcNow.AddMinutes(-2), paymentIntentId: $"pi_pc12_{Guid.NewGuid():N}");

        var ics = await PublicFeedAsync(await ExportPathAsync(owner, property.Id));

        Assert.DoesNotContain($"booking-{expired}", ics);
        Assert.DoesNotContain($"{PublicAvailabilityPostgresTests.NextYear(11, 1):yyyyMMdd}", ics);
        Assert.Contains($"UID:booking-{valid}", ics.Split("\r\n"));
    }

    [PostgresFact]
    public async Task PublicExport_PendingOnSiteRequest_IsNotExportedUntilTheHostAccepts()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc12-onsite");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var pending = await SeedBookingAsync(
            property, PublicAvailabilityPostgresTests.NextYear(12, 1), PublicAvailabilityPostgresTests.NextYear(12, 3),
            b =>
            {
                b.PaymentOption = PaymentOption.OnSite;
                b.GuestEmailVerifiedAt = DateTime.UtcNow.AddMinutes(-5);
                b.RequestExpiresAt = DateTime.UtcNow.AddHours(20);
            });
        var accepted = await SeedBookingAsync(
            property, PublicAvailabilityPostgresTests.NextYear(12, 10), PublicAvailabilityPostgresTests.NextYear(12, 12),
            b =>
            {
                b.PaymentOption = PaymentOption.OnSite;
                b.Status = BookingStatus.Confirmed;
            });

        var ics = await PublicFeedAsync(await ExportPathAsync(owner, property.Id));

        Assert.DoesNotContain($"booking-{pending}", ics);
        Assert.Contains($"UID:booking-{accepted}", ics.Split("\r\n"));
    }

    [PostgresFact]
    public async Task RegenerateExportUrl_Owner_OldLinkAnswers404AndTheNewOneServesTheFeed()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc12-regen");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var oldPath = await ExportPathAsync(owner, property.Id);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(oldPath)).StatusCode);

        var response = await owner.PostAsync($"/api/properties/{property.Id}/ical/export-url/regenerate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newPath = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exportUrl").GetString()!).AbsolutePath;
        Assert.NotEqual(oldPath, newPath);
        Assert.StartsWith("/api/public/ical/", newPath);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(oldPath)).StatusCode);
        var fresh = await anonymous.GetAsync(newPath);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Contains("BEGIN:VCALENDAR", await fresh.Content.ReadAsStringAsync());
        // The host keeps seeing (and copying) the new link.
        Assert.Equal(newPath, await ExportPathAsync(owner, property.Id));
        var status = await owner.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/ical/status");
        Assert.Equal(newPath, new Uri(status.GetProperty("exportUrl").GetString()!).AbsolutePath);
    }

    [PostgresFact]
    public async Task RegenerateExportUrl_HostOfAnotherOrg_Returns404AndTheLinkKeepsWorking()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc12-owner");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var path = await ExportPathAsync(owner, property.Id);
        var otherHost = $"auth0|pc12-other-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(otherHost);
        using var other = _factory.CreateAuthenticatedClient(otherHost, HostRole);

        var response = await other.PostAsync($"/api/properties/{property.Id}/ical/export-url/regenerate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(path, await ExportPathAsync(owner, property.Id));
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(path)).StatusCode);
    }

    // Path of the export link shown to the host (created on first use).
    private static async Task<string> ExportPathAsync(HttpClient owner, Guid propertyId)
    {
        var body = await owner.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/ical/export-url");
        return new Uri(body.GetProperty("exportUrl").GetString()!).AbsolutePath;
    }

    private async Task<string> PublicFeedAsync(string path)
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // What the queued Hangfire job does (the test host mocks the job client).
    private async Task SyncAsync(Guid feedId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncFeedAsync(feedId);
    }

    /// <summary>A direct booking, Pending by default (a checkout hold), with a Stripe PaymentIntent when given.</summary>
    private async Task<Guid> SeedBookingAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        Action<Booking> configure,
        string? paymentIntentId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario.{Guid.NewGuid():N}@example.com",
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
            TotalPrice = 300m,
            SpecialRequests = "Citofono Rossi",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        configure(booking);
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
                CreatedAt = booking.CreatedAt,
                UpdatedAt = booking.CreatedAt,
            });
        }

        await db.SaveChangesAsync();
        return booking.Id;
    }
}
