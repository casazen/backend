using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-04 (A5-09): the wire contract of the cockpit read by the web app (<c>src/lib/compliance-routes.ts</c>) and by the
/// E2E mock (<c>e2e/helpers/compliance-mock.ts</c>). Each item carries the action by name and the id of its target;
/// no front-end path, which the API used to invent (<c>/bookings/{id}/check-in</c>, ...) and the web app sent to the
/// dashboard.
/// </summary>
public class ComplianceSummaryContractTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";
    private readonly CasazenWebApplicationFactory _factory;

    public ComplianceSummaryContractTests(CasazenWebApplicationFactory factory) => _factory = factory;

    /// <summary>Fixed clock (FD-06): 22:30 UTC on 24/09 is already 25/09 in Rome, the day the cockpit counts.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 22, 30, 0, TimeSpan.Zero));

    private static readonly DateTime Today = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSummary_EveryItem_ReturnsActionByNameAndTargetIdWithoutRouteLink()
    {
        Assert.Equal(Today, Clock.TodayInRome());
        var hostId = $"auth0|co04-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        var departing = await SeedStayAsync(property, Today.AddDays(-2), Today, alloggiati: null);
        var rejected = await SeedStayAsync(property, Today.AddDays(-1), Today.AddDays(2), AlloggiatiWebStatus.Rifiutato);
        await using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        }));
        using var host = CreateHostClient(app, hostId);

        var summary = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");

        AssertItem(summary, "propertiesPending", property.Id, "ActivateProperty", "propertyId");
        AssertItem(summary, "guestCheckInsIncomplete", departing, "CompleteGuestCheckIn", "bookingId");
        AssertItem(summary, "checkoutsDue", departing, "CheckOut", "bookingId");
        AssertItem(summary, "alloggiatiManualRequired", departing, "SendAlloggiati", "bookingId");
        AssertItem(summary, "alloggiatiFailures", rejected, "ResolveAlloggiatiFailure", "bookingId");
        foreach (var section in summary.EnumerateObject())
        {
            foreach (var item in section.Value.GetProperty("items").EnumerateArray())
                Assert.False(item.TryGetProperty("routeLink", out _), $"{section.Name}: routeLink is gone (A5-09)");
        }
    }

    private static HttpClient CreateHostClient(WebApplicationFactory<Program> app, string hostId)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", hostId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", HostRole);
        return client;
    }

    private static void AssertItem(JsonElement summary, string section, Guid targetId, string action, string targetProperty)
    {
        var item = summary.GetProperty(section).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == targetId);
        Assert.Equal(action, item.GetProperty("action").GetString());
        Assert.Equal(targetId, item.GetProperty(targetProperty).GetGuid());
        var otherTarget = targetProperty == "propertyId" ? "bookingId" : "propertyId";
        Assert.Equal(JsonValueKind.Null, item.GetProperty(otherTarget).ValueKind);
    }

    /// <summary>A stay with incomplete guest data (no stay guests), inserted directly like the other cockpit tests.</summary>
    private async Task<Guid> SeedStayAsync(Property property, DateTime checkIn, DateTime checkOut, AlloggiatiWebStatus? alloggiati)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            Status = BookingStatus.CheckedIn,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);
        if (alloggiati is { } status)
        {
            db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
            {
                BookingId = booking.Id,
                GuestId = guest.Id,
                OrgId = property.OrgId,
                Status = status,
            });
        }

        await db.SaveChangesAsync();
        return booking.Id;
    }
}
