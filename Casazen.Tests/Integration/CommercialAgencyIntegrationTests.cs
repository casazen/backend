using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class CommercialAgencyIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task NewAgency_CreatesNamedOrganization_AndMakesFounderItsOwnerAdministrator()
    {
        var userId = $"auth0|agency-founder-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        var response = await client.PostAsJsonAsync("/api/users/onboarding", new
        {
            rentalType = "ShortTerm",
            commercialSignup = new { profile = "agency", agencyName = "Nuova Agenzia", planTier = "Agency", rooms = 3, addons = Array.Empty<string>() },
            consents = new
            {
                tosAccepted = true, tosVersion = "2026-06-v1",
                privacyAccepted = true, privacyVersion = "2026-06-v1",
                dpaAccepted = true, dpaVersion = "2026-06-v1",
                subprocessorsAcknowledged = true, subprocessorsVersion = "2026-06-v1",
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        var org = await db.Orgs.SingleAsync(o => o.Id == user.OrgId);
        Assert.Equal("Nuova Agenzia", org.Name);
        Assert.False(org.CommercialPaymentConfirmed);
        var founder = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == userId);
        Assert.Equal(org.Id, founder.OrgId);
        Assert.Equal(OrgRole.Owner, founder.Role);
        Assert.Contains(await db.UserContextMemberships.IgnoreQueryFilters().Where(m => m.UserId == userId).Include(m => m.Role).ToListAsync(),
            m => m.ContextKey == "account" && m.Role.RoleKey == "org_owner");
        // Creation never grants the platform-wide Admin role.
        Assert.NotEqual(Casazen.Core.Entities.UserRole.Admin, user.Role);
    }
}
