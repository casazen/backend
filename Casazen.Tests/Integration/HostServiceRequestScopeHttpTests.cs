using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-08a, who sees the interventions: the list of the host and the counters of its chips reach the properties of the caller and no
/// others. A collaborator "Solo alcuni" sees, counts and filters only the requests of the property it was given; asking for the other
/// one is refused, as the lists of the stays are. On PostgreSQL in CI (the team's roles are seeded by the migrations).
/// </summary>
public class HostServiceRequestScopeHttpTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    [PostgresFact]
    public async Task List_ACollaboratorSoloAlcuni_SeesAndCountsOnlyTheRequestsOfThePropertyItWasGiven()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        Property granted;
        Property hidden;
        Guid memberId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (granted, hidden, _) = await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId);
            memberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == collaboratorId).Select(m => m.Id).SingleAsync();
        }

        // The scenario has one new request on each property.
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var collaborator = factory.CreateAuthenticatedClient(collaboratorId);
        Assert.Equal(2, await TotalAsync(owner));
        Assert.Equal(2, await TotalAsync(collaborator));

        var limited = await owner.PutAsJsonAsync(
            $"/api/orgs/me/members/{memberId}/properties",
            new { propertyScope = "Selected", propertyIds = new[] { granted.Id } });
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);

        // The owner still sees both; the collaborator the one it was given, in the list and in the counters of every chip.
        Assert.Equal(2, await TotalAsync(owner));
        var list = await ReadJsonAsync(await collaborator.GetAsync("/api/service-requests?view=pending"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(granted.Id, list.GetProperty("items")[0].GetProperty("propertyId").GetGuid());

        var counts = await ReadJsonAsync(await collaborator.GetAsync("/api/service-requests/counts"));
        Assert.Equal(1, counts.GetProperty("all").GetInt32());
        Assert.Equal(1, counts.GetProperty("pending").GetInt32());
        Assert.Equal(2, (await ReadJsonAsync(await owner.GetAsync("/api/service-requests/counts"))).GetProperty("all").GetInt32());

        // The property it was not given is refused, whichever way it is asked for.
        foreach (var url in new[] { $"/api/service-requests?propertyId={hidden.Id}", $"/api/service-requests/counts?propertyId={hidden.Id}" })
        {
            var refused = await collaborator.GetAsync(url);
            Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{url} answered {(int)refused.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync($"/api/service-requests?propertyId={granted.Id}")).StatusCode);
    }

    private static async Task<int> TotalAsync(HttpClient client) =>
        (await ReadJsonAsync(await client.GetAsync("/api/service-requests"))).GetProperty("total").GetInt32();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
