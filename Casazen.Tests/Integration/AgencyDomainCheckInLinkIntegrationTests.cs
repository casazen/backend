using System.Net;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US01/T03 (NF-142, AC-NF142-4): links already sent keep working. The check-in link of an org whose verified domain serves its guest area is
/// built on that domain, and its token opens through the CasaZen endpoint whatever Host or Origin the request carries: nothing about a token depends
/// on the host of its link, and no route, token or endpoint changed.
/// </summary>
public class AgencyDomainCheckInLinkIntegrationTests(SiteIntegrationFlagsFactory factory) : IClassFixture<SiteIntegrationFlagsFactory>
{
    [Fact]
    public async Task CheckInToken_OfAnOrgOnItsOwnDomain_OpensWhateverHostOrOrigin()
    {
        var seed = await factory.SeedConfirmedBookingWithTokenAsync();
        var domain = $"prenota.{Guid.NewGuid():N}.example.test";
        Guid orgId;
        using (var scope = factory.Services.CreateScope())
        {
            var booking = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.FindAsync(seed.BookingId);
            orgId = booking!.OrgId;
        }

        await OrgDomainSurfacesIntegrationTests.SetPlanAsync(factory, orgId, PlanTier.Pro);
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(factory, orgId, domain);
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(factory, orgId, o =>
        {
            o.GuestAreaOnOwnDomain = true;
            o.UseOwnDomainForLinks = true;
        });
        var token = await IssueLinkAsync(seed.BookingId, orgId);

        // The link a message carries is on the agency's domain...
        using (var scope = factory.Services.CreateScope())
        {
            var links = await scope.ServiceProvider.GetRequiredService<AgencyDomainLinks>().ForOrgAsync(orgId);
            Assert.Equal($"https://{domain}/checkin/{token}", links.GuestCheckIn(token));
        }

        // ...and its token opens through the CasaZen endpoint, whatever the request says about where it comes from.
        var requests = new (string? Host, string? Origin)[]
        {
            (null, null),
            (domain, null),
            (domain, $"https://{domain}"),
            (null, "https://www.altra-agenzia.example.test"),
        };
        foreach (var (host, origin) in requests)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/checkin/{token}");
            if (host is not null)
                request.Headers.Host = host;
            if (origin is not null)
                request.Headers.Add("Origin", origin);

            using var response = await factory.CreateClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private async Task<string> IssueLinkAsync(Guid bookingId, Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var issued = await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance).IssueLinkAsync(bookingId, orgId);
        return issued.Token;
    }
}
