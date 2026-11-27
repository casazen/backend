using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The test host with the guest portal on (<c>Features:GuestPortal</c>), so its endpoints answer, and the e-mails recorded instead of queued (E50/F01/US03/T03), so a test can tell that a
/// call sent none.
/// </summary>
public class GuestPortalEnabledFactory : CasazenWebApplicationFactory
{
    private readonly RecordingEmailQueue _emails = new();

    /// <summary>What the host queued so far: recipient, content and template name.</summary>
    public IReadOnlyList<(string? To, EmailContent Content, string Template)> SentEmails() => _emails.Snapshot();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:GuestPortal"] = "true", ["Features:GuestPortalAutoReplies"] = "true" }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(_emails);
        });
    }
}

/// <summary>
/// E50/F01/US01/T01 (NF-152, AC-NF152-1) and E50/F01/US03/T03 (the e-mail again): the host endpoints of the stay link, over the real pipeline on PostgreSQL (the roles come from the migration
/// seed). A booking of another org is a 404 for every call, a person who may only read bookings gets 403 on every change and never the address,
/// and with the flag off every endpoint is a 404 before authentication. «Send it again» (<c>POST …/resend</c>) is in all three: it writes to the e-mail address of a guest, so a call that is
/// refused sends nothing.
/// </summary>
public class GuestPortalLinkIntegrationTests(GuestPortalEnabledFactory factory) : IClassFixture<GuestPortalEnabledFactory>
{
    /// <summary>Permission rule (tenant): the five calls on a booking of another org answer the same 404, and nothing is written or sent for it.</summary>
    [PostgresFact]
    public async Task Host_BookingOfAnotherOrg_Returns404()
    {
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var (otherOwnerId, otherOrgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var otherBooking = await AddBookingAsync(otherOrgId, otherOwnerId);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        var queuedBefore = factory.SentEmails().Count;

        await OrgTeamHttp.AssertProblemAsync(await owner.GetAsync(Link(otherBooking)), HttpStatusCode.NotFound, "not_found");
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync(Link(otherBooking), null), HttpStatusCode.NotFound, "not_found");
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync(Link(otherBooking, "/regenerate"), null), HttpStatusCode.NotFound, "not_found");
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync(Link(otherBooking, "/resend"), null), HttpStatusCode.NotFound, "not_found");
        await OrgTeamHttp.AssertProblemAsync(await owner.DeleteAsync(Link(otherBooking)), HttpStatusCode.NotFound, "not_found");

        Assert.Empty(await LinksOfAsync(otherBooking));
        Assert.Equal(queuedBefore, factory.SentEmails().Count);
    }

    /// <summary>
    /// Permission rule: the collaborator (<c>booking.read</c>, no <c>booking.write</c>) reads the state of the link but never gets its address, and
    /// gets 403 on the first link, the regeneration, the e-mail again (nothing is sent) and the revocation, which change nothing; the owner gets the link, the same
    /// one again, a new one, and revokes it.
    /// </summary>
    [PostgresFact]
    public async Task Host_WithoutBookingWrite_Regenerate_Returns403()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        var bookingId = await AddBookingAsync(orgId, ownerId);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var collaborator = factory.CreateAuthenticatedClient(collaboratorId);

        var none = await owner.GetFromJsonAsync<JsonElement>(Link(bookingId));
        Assert.Equal("none", none.GetProperty("state").GetString());
        Assert.True(none.GetProperty("eligible").GetBoolean());

        var created = await owner.PostAsync(Link(bookingId), null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var firstUrl = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString();
        Assert.Matches("/s/[A-Za-z0-9_-]{32}$", firstUrl);
        var again = await owner.PostAsync(Link(bookingId), null);
        Assert.Equal(firstUrl, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());

        // The collaborator reads the state, never the address, and changes nothing: no new link, no revocation, and no e-mail to the guest.
        var queuedBefore = factory.SentEmails().Count;
        var read = await collaborator.GetFromJsonAsync<JsonElement>(Link(bookingId));
        Assert.Equal("active", read.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("url").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsync(Link(bookingId, "/regenerate"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsync(Link(bookingId, "/resend"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsync(Link(bookingId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.DeleteAsync(Link(bookingId))).StatusCode);
        Assert.Equal(queuedBefore, factory.SentEmails().Count);

        var kept = Assert.Single(await LinksOfAsync(bookingId));
        Assert.Null(kept.RevokedAt);
        Assert.EndsWith(kept.Token, firstUrl, StringComparison.Ordinal);

        // The owner regenerates (a new address, the old one revoked) and revokes.
        var regenerated = await owner.PostAsync(Link(bookingId, "/regenerate"), null);
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.NotEqual(firstUrl, (await regenerated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Link(bookingId))).StatusCode);

        var rows = await LinksOfAsync(bookingId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
        Assert.Equal("none", (await owner.GetFromJsonAsync<JsonElement>(Link(bookingId))).GetProperty("state").GetString());
    }

    /// <summary>Permission rule (the flag): off, every endpoint of the link answers 404 before authentication, signed in or not.</summary>
    [Fact]
    public async Task Host_FlagOff_Returns404()
    {
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:GuestPortal"] = "false" })));
        using var anonymous = off.CreateClient();
        using var signedIn = off.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        signedIn.DefaultRequestHeaders.Add("X-Test-User", TestAuthHandler.DefaultUserId);
        signedIn.DefaultRequestHeaders.Add("X-Test-Roles", "PropertyOwner");
        var bookingId = Guid.NewGuid();

        foreach (var client in new[] { anonymous, signedIn })
        {
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.GetAsync(Link(bookingId)));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.PostAsync(Link(bookingId), null));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.PostAsync(Link(bookingId, "/regenerate"), null));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.PostAsync(Link(bookingId, "/resend"), null));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.DeleteAsync(Link(bookingId)));
        }
    }

    private static string Link(Guid bookingId, string suffix = "") => $"/api/bookings/{bookingId}/guest-portal{suffix}";

    /// <summary>A confirmed stay from a portal at a new property of the org, arriving in five days.</summary>
    private async Task<Guid> AddBookingAsync(Guid orgId, string ownerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = HostScopeScenario.NewProperty(orgId, ownerId, "Casa del portale");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Ospite", Email = $"{Guid.NewGuid():N}@example.com" };
        var today = TimeProvider.System.TodayInRome();
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(5),
            CheckOutDate = today.AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<List<GuestPortalLink>> LinksOfAsync(Guid bookingId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GuestPortalLinks.IgnoreQueryFilters().AsNoTracking().Where(l => l.BookingId == bookingId).ToListAsync();
    }
}
