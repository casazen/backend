using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Permission rule (the token is the tenant of a portal request, whoever is signed in): a signed-in host of ANOTHER org and a signed-in user with no org
    /// at all open the stay of the token exactly as an anonymous guest does, though the tenant filter of their own request matches nothing of it; the stay
    /// is the one of the token, never the caller's, and a token that opens nothing is the same private 404 for them too.
    /// </summary>
    [PostgresFact]
    public async Task Overview_SignedInCallerOfAnotherOrgOrWithNoOrg_OpensTheStayOfTheToken()
    {
        var token = await SeedStayWithLinkAsync();
        var (otherOwnerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var otherHost = factory.CreateAuthenticatedClient(otherOwnerId, roles: "PropertyOwner");
        using var noOrg = factory.CreateAuthenticatedClient($"auth0|ospite-senza-org-{Guid.NewGuid():N}");

        foreach (var client in new[] { otherHost, noOrg })
        {
            var response = await client.GetAsync($"/api/public/guest-portal/{token}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var stay = body.RootElement.GetProperty("stay");
            Assert.Equal("Casa del Portale", stay.GetProperty("propertyName").GetString());
            Assert.Equal("Ostuni", stay.GetProperty("city").GetString());
            Assert.Equal("confirmed", stay.GetProperty("status").GetString());
            Assert.False(body.RootElement.GetProperty("canSeeReserved").GetBoolean());

            var details = await client.GetAsync($"/api/public/guest-portal/{token}/stay");
            Assert.Equal(HttpStatusCode.OK, details.StatusCode);

            var unknown = await client.GetAsync("/api/public/guest-portal/Unknown_Token_0000000000000000000");
            await OrgTeamHttp.AssertProblemAsync(unknown, HttpStatusCode.NotFound, GuestPortalRules.LinkInvalidCode);
            Assert.Equal("no-store", unknown.Headers.CacheControl?.ToString());
        }
    }

    [PostgresFact]
    public async Task StayAndChat_UseTheTokenAndProtectTheGuide()
    {
        var token = await SeedStayWithLinkAsync();
        using var client = factory.CreateClient();
        var path = $"/api/public/guest-portal/{token}";

        var stay = await client.GetStringAsync($"{path}/stay");
        Assert.Contains("Informazioni pubbliche", stay, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, stay, StringComparison.Ordinal);
        Assert.DoesNotContain("wifi-riservato", stay, StringComparison.Ordinal);

        var send = new HttpRequestMessage(HttpMethod.Post, $"{path}/chat")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { text = "A che ora posso arrivare?" })
        };
        send.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var sent = await client.SendAsync(send);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var chat = await client.GetStringAsync($"{path}/chat");
        Assert.Contains("A che ora posso arrivare?", chat, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/cancellation")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"{path}/review")).StatusCode);
        Assert.Equal("[]", await client.GetStringAsync($"{path}/documents"));
    }

    [PostgresFact]
    public async Task PrivacyRequests_AreIdempotentAndScopedToTheStay()
    {
        var token = await SeedStayWithLinkAsync();
        var otherToken = await SeedStayWithLinkAsync();
        using var client = factory.CreateClient();
        var path = $"/api/public/guest-portal/{token}";
        var endpoint = $"{path}/privacy/requests";

        using var copy = await client.PostAsJsonAsync(endpoint, new { kind = "copy" });
        using var repeated = await client.PostAsJsonAsync(endpoint, new { kind = "copy" });
        using var erasure = await client.PostAsJsonAsync(endpoint, new { kind = "erasure" });
        Assert.Equal(HttpStatusCode.Accepted, copy.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, erasure.StatusCode);

        using var requests = JsonDocument.Parse(await client.GetStringAsync(endpoint));
        Assert.Equal(2, requests.RootElement.GetArrayLength());
        Assert.All(requests.RootElement.EnumerateArray(), request =>
        {
            Assert.Equal(JsonValueKind.Null, request.GetProperty("completedAt").ValueKind);
            Assert.Equal(30, (request.GetProperty("deadline").GetDateTime() -
                request.GetProperty("createdAt").GetDateTime()).TotalDays, precision: 3);
        });
        Assert.Equal("[]", await client.GetStringAsync($"/api/public/guest-portal/{otherToken}/privacy/requests"));
        using var foreign = await client.PostAsync($"{path}/documents/{Guid.NewGuid()}/correction-request", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [PostgresFact]
    public async Task PortalCheckIn_RejectsMissingOrOutdatedHouseRulesAcknowledgement()
    {
        var token = await SeedStayWithLinkAsync(withCheckIn: true);
        using var client = factory.CreateClient();
        var path = $"/api/public/checkin/stay_{token}";

        using var context = JsonDocument.Parse(await client.GetStringAsync(path));
        Assert.Equal("Rispetta il silenzio.", context.RootElement.GetProperty("houseRules").GetString());
        Assert.Equal(64, context.RootElement.GetProperty("houseRulesVersion").GetString()!.Length);

        using var refused = await client.PostAsync(path, System.Net.Http.Json.JsonContent.Create(new { guests = Array.Empty<object>() }));
        await OrgTeamHttp.AssertProblemAsync(refused, HttpStatusCode.Conflict, "guest_house_rules_acknowledgement_required");
    }

    [PostgresFact]
    public async Task GuestAccount_OpensOnlyStaysOfItsVerifiedEmail()
    {
        var token = await SeedStayWithLinkAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.GuestPortalLinks.SingleAsync(l => l.Token == token);
        var email = await db.Bookings.Where(b => b.Id == link.BookingId).Select(b => b.Guest.Email).SingleAsync();
        using var matching = factory.CreateAuthenticatedClient($"auth0|guest-{Guid.NewGuid():N}", email: email, emailVerified: true);
        using var unverified = factory.CreateAuthenticatedClient($"auth0|guest-{Guid.NewGuid():N}", email: email, emailVerified: false);
        using var other = factory.CreateAuthenticatedClient($"auth0|guest-{Guid.NewGuid():N}", email: $"{Guid.NewGuid():N}@example.com", emailVerified: true);

        var path = $"/api/guest/stays/{link.BookingId:D}/open";
        Assert.Equal(HttpStatusCode.Forbidden, (await unverified.GetAsync("/api/guest/stays")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(path, null)).StatusCode);
        using var response = await matching.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.EndsWith($"/s/{token}", body.RootElement.GetProperty("url").GetString());
    }

    /// <summary>A confirmed stay from a portal at a house of a new org, arriving in five days, and its stay link.</summary>
    private async Task<string> SeedStayWithLinkAsync(bool withCheckIn = false)
    {
        var ownerId = $"auth0|portale-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = HostScopeScenario.NewProperty(org.Id, ownerId, "Casa del Portale");
        property.Address = Address;
        property.GuestHouseGuideJson = scope.ServiceProvider.GetRequiredService<GuestHouseGuideStorage>().Protect(property.Id, new GuestHouseGuide
        {
            Arrival = "Informazioni pubbliche",
            Wifi = "wifi-riservato",
        });
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
        if (withCheckIn)
        {
            property.HouseRules = "Rispetta il silenzio.";
            db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = org.Id,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            });
        }
        await db.SaveChangesAsync();

        var links = scope.ServiceProvider.GetRequiredService<IGuestPortalLinkService>();
        return (await links.GetOrCreateAsync(org.Id, booking.Id, createdByUserId: null))!.Link.Token;
    }
}
