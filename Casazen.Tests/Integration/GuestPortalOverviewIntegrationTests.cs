using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E50/F01/US01/T02 (NF-153, AC-NF153-1, AC-NF153-3), over the real pipeline (PostgreSQL in CI), like <see cref="CrmMarketingFeatureFlagTests"/>: the
/// overview of the guest portal answers an anonymous caller from the token alone, with the shape of the contract and nothing reserved before the
/// check-in; a token that opens nothing is the same private 404; with the flag off the portal is a route that does not exist.
/// </summary>
public class GuestPortalOverviewIntegrationTests(GuestPortalEnabledFactory factory) : IClassFixture<GuestPortalEnabledFactory>
{
    private const string Address = "Via Riservata 12, scala B";

    /// <summary>
    /// Privacy rule: before the check-in the overview says <c>canSeeReserved: false</c> and carries nothing reserved: the address of the house is
    /// nowhere in the answer, only its name and its town. The answer is private and has exactly the keys of the contract.
    /// </summary>
    [PostgresFact]
    public async Task Overview_BeforeCheckIn_ReturnsNoReservedData()
    {
        var token = await SeedStayWithLinkAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/guest-portal/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", string.Join(",", response.Headers.GetValues("X-Robots-Tag")));
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        var root = body.RootElement;
        Assert.Equal(
            ["stay", "branding", "language", "sections", "nextStep", "canSeeReserved", "checkInCompleted", "features"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.False(root.GetProperty("canSeeReserved").GetBoolean());
        Assert.False(root.GetProperty("checkInCompleted").GetBoolean());
        Assert.Equal("Casa del Portale", root.GetProperty("stay").GetProperty("propertyName").GetString());
        Assert.Equal("Ostuni", root.GetProperty("stay").GetProperty("city").GetString());
        Assert.Equal("confirmed", root.GetProperty("stay").GetProperty("status").GetString());
        Assert.True(root.GetProperty("stay").GetProperty("isOta").GetBoolean());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", root.GetProperty("stay").GetProperty("checkIn").GetString());
        Assert.Contains(root.GetProperty("sections").EnumerateArray(), s => s.GetProperty("key").GetString() == "stay");
        Assert.Equal("stay.welcome", root.GetProperty("nextStep").GetProperty("key").GetString());
        Assert.DoesNotContain(Address, json, StringComparison.Ordinal);
        Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);

        // A token that opens nothing: the same private 404 of the portal.
        var unknown = await client.GetAsync("/api/public/guest-portal/Unknown_Token_0000000000000000000");
        await OrgTeamHttp.AssertProblemAsync(unknown, HttpStatusCode.NotFound, GuestPortalRules.LinkInvalidCode);
        Assert.Equal("no-store", unknown.Headers.CacheControl?.ToString());
    }

    /// <summary>Permission rule (the flag): off, the overview is a 404 like a route that does not exist, even with a real token.</summary>
    [Fact]
    public async Task Overview_FlagOff_Returns404()
    {
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:GuestPortal"] = "false" })));
        using var client = off.CreateClient();

        var response = await client.GetAsync("/api/public/guest-portal/AbCdEfGhIjKlMnOpQrStUvWxYz012345");

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    /// <summary>A confirmed stay from a portal at a house of a new org, arriving in five days, and its stay link.</summary>
    private async Task<string> SeedStayWithLinkAsync()
    {
        var ownerId = $"auth0|portale-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = HostScopeScenario.NewProperty(org.Id, ownerId, "Casa del Portale");
        property.Address = Address;
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Ospite", Email = $"{Guid.NewGuid():N}@example.com" };
        var today = TimeProvider.System.TodayInRome();
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(5),
            CheckOutDate = today.AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.BookingCom,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var links = scope.ServiceProvider.GetRequiredService<IGuestPortalLinkService>();
        return (await links.GetOrCreateAsync(org.Id, booking.Id, createdByUserId: null))!.Link.Token;
    }
}
