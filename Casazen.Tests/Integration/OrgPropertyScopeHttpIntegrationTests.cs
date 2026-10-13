using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03 over the real pipeline: the owner gives a collaborator "Solo alcuni" some properties of the org, and the collaborator,
/// who exists only in the database (a token with a subject and no roles), sees those and nothing else on every list and on
/// each single resource; the change is seen on the very next request (the authorization cache is invalidated by the write).
/// Plus the refusals of the endpoints that set the access, with their status and code. On PostgreSQL in CI; on the in-memory
/// fallback locally (the seed data of the model is written first).
/// </summary>
public class OrgPropertyScopeHttpIntegrationTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    private sealed record World(
        string OwnerId,
        Guid OrgId,
        string CollaboratorId,
        Guid CollaboratorMemberId,
        Guid GrantedId,
        Guid HiddenId,
        Guid HiddenBookingId);

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    private HttpClient Owner(World world) => factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");

    /// <summary>A collaborator is a person of the database only: no role in the token, nothing it could widen its reach with.</summary>
    private HttpClient Collaborator(World world) => factory.CreateAuthenticatedClient(world.CollaboratorId);

    /// <summary>An org with its owner, a collaborator (every property, as a new member is) and two properties full of data.</summary>
    private async Task<World> NewWorldAsync()
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (granted, hidden, _) = await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId);
        var memberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == collaboratorId).Select(m => m.Id).SingleAsync();
        var hiddenBookingId = await db.Bookings.IgnoreQueryFilters().Where(b => b.PropertyId == hidden.Id).Select(b => b.Id).FirstAsync();
        return new World(ownerId, orgId, collaboratorId, memberId, granted.Id, hidden.Id, hiddenBookingId);
    }

    private async Task<Guid> MemberRowOfAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == userId).Select(m => m.Id).SingleAsync();
    }

    private static object Selected(params Guid[] propertyIds) => new { propertyScope = "Selected", propertyIds };

    private static async Task<HashSet<Guid>> IdsAsync(HttpClient client, string path, string property = "id")
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = rows.ValueKind == JsonValueKind.Array ? rows : rows.GetProperty("items");
        return items.EnumerateArray().Select(row => row.GetProperty(property).GetGuid()).ToHashSet();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- The whole journey ---------------------------------------------------------------------------

    [Fact]
    public async Task Journey_AnOwnerLimitsACollaborator_AndItSeesOnlyItsPropertiesEverywhere()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        var access = $"/api/orgs/me/members/{world.CollaboratorMemberId}/properties";

        // A new collaborator reaches every property of the org, as before this change.
        Assert.Equal([world.GrantedId, world.HiddenId], await IdsAsync(collaborator, "/api/properties"));
        var before = await JsonAsync(await owner.GetAsync(access));
        Assert.Equal(("All", true), (before.GetProperty("propertyScope").GetString(), before.GetProperty("scopeSupported").GetBoolean()));

        // The owner limits it to one property; the page says who reaches what.
        var limited = await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.GrantedId)));
        Assert.Equal("Selected", limited.GetProperty("propertyScope").GetString());
        var rows = limited.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("propertyId").GetGuid());
        Assert.True(rows[world.GrantedId].GetProperty("granted").GetBoolean());
        Assert.False(rows[world.HiddenId].GetProperty("granted").GetBoolean());
        // Who can access: the owner and the collaborator reach the first, the owner alone the second.
        Assert.Equal((2, 1), (rows[world.GrantedId].GetProperty("peopleWithAccess").GetInt32(), rows[world.HiddenId].GetProperty("peopleWithAccess").GetInt32()));

        // At once, on this instance: the properties, each single one, the stays, the dashboard, the interventions.
        Assert.Equal([world.GrantedId], await IdsAsync(collaborator, "/api/properties"));
        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync($"/api/properties/{world.GrantedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/properties/{world.HiddenId}")).StatusCode);

        var bookings = await IdsAsync(collaborator, "/api/bookings", "propertyId");
        Assert.Equal([world.GrantedId], bookings);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/bookings?propertyId={world.HiddenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await collaborator.GetAsync($"/api/bookings/{world.HiddenBookingId}")).StatusCode);

        var kpis = await JsonAsync(await collaborator.GetAsync("/api/dashboard/kpis"));
        Assert.Equal(1, kpis.GetProperty("propertyCount").GetInt32());

        var requests = await JsonAsync(await collaborator.GetAsync("/api/service-requests"));
        Assert.NotEmpty(requests.GetProperty("items").EnumerateArray());
        Assert.All(requests.GetProperty("items").EnumerateArray(), item => Assert.Equal(world.GrantedId, item.GetProperty("propertyId").GetGuid()));

        // The owner is not limited by what it did to somebody else.
        Assert.Equal([world.GrantedId, world.HiddenId], await IdsAsync(owner, "/api/properties"));
        Assert.Equal([world.GrantedId, world.HiddenId], await IdsAsync(owner, "/api/bookings", "propertyId"));
        Assert.Equal(2, (await JsonAsync(await owner.GetAsync("/api/dashboard/kpis"))).GetProperty("propertyCount").GetInt32());

        // Back to every property, and then to none: both seen on the next request.
        await JsonAsync(await owner.PutAsJsonAsync(access, new { propertyScope = "All" }));
        Assert.Equal([world.GrantedId, world.HiddenId], await IdsAsync(collaborator, "/api/properties"));

        await JsonAsync(await owner.PutAsJsonAsync(access, Selected()));
        Assert.Empty(await IdsAsync(collaborator, "/api/properties"));
        Assert.Empty(await IdsAsync(collaborator, "/api/bookings", "propertyId"));
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/properties/{world.GrantedId}")).StatusCode);
    }

    [Fact]
    public async Task Limited_ACollaboratorKeepsTheLimitsOfItsRole_MoneyAndThePeopleStayClosed()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        var access = $"/api/orgs/me/members/{world.CollaboratorMemberId}/properties";
        await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.GrantedId)));

        // property.read and booking.read are the collaborator ones; payments, the fiscal area and the people of the org are not.
        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync("/api/bookings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/fiscal/tax-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/fiscal/reports/annual/2026")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/orgs/me/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync(access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PutAsJsonAsync(access, new { propertyScope = "All" })).StatusCode);
    }

    [Fact]
    public async Task Limited_ACollaboratorCannotTouchAPropertyItWasNotGiven()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));

        // The booking of a hidden property is not found at all, and the in-charge person is not the collaborator to set.
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PutAsJsonAsync($"/api/properties/{world.HiddenId}/responsible", new { userId = world.CollaboratorId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await collaborator.GetAsync($"/api/bookings/{world.HiddenBookingId}")).StatusCode);
    }

    // --- What the endpoints refuse ---------------------------------------------------------------------

    [Fact]
    public async Task Put_WhatCannotBeSet_IsRefusedWithItsOwnCode()
    {
        var world = await NewWorldAsync();
        var manager = await OrgTeamHttp.AddMemberAsync(factory, world.OrgId, OrgRole.PropertyManager, ["short-rent"]);
        var managerRow = await MemberRowOfAsync(manager);
        using var owner = Owner(world);
        var access = $"/api/orgs/me/members/{world.CollaboratorMemberId}/properties";

        // Only a collaborator can be limited: the manager reaches the whole org.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/orgs/me/members/{managerRow}/properties", Selected(world.GrantedId)),
            HttpStatusCode.UnprocessableEntity,
            "org_member_scope_not_supported");
        var view = await JsonAsync(await owner.GetAsync($"/api/orgs/me/members/{managerRow}/properties"));
        Assert.False(view.GetProperty("scopeSupported").GetBoolean());

        // A property that is not of the org (unknown, or of another org) is never granted.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync(access, Selected(Guid.NewGuid())), HttpStatusCode.UnprocessableEntity, "org_member_property_unknown");
        var other = await factory.SeedPropertyAsync($"auth0|am03-other-{Guid.NewGuid():N}");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync(access, Selected(other.Id)), HttpStatusCode.UnprocessableEntity, "org_member_property_unknown");

        // The request itself: a scope is required, and the list is bounded.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(access, new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.PutAsJsonAsync(access, Selected(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray()))).StatusCode);

        // Nobody here: 404 for a member the org does not have.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.GetAsync($"/api/orgs/me/members/{Guid.NewGuid()}/properties"), HttpStatusCode.NotFound, "org_member_not_found");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/orgs/me/members/{Guid.NewGuid()}/properties", new { propertyScope = "All" }),
            HttpStatusCode.NotFound,
            "org_member_not_found");

        // Nothing of the above changed what the collaborator reaches.
        using var collaborator = Collaborator(world);
        Assert.Equal([world.GrantedId, world.HiddenId], await IdsAsync(collaborator, "/api/properties"));
    }

    [Fact]
    public async Task Put_AnAdministratorLimitsACollaborator_ButAnotherAdministratorIsForTheOwnerOnly()
    {
        var world = await NewWorldAsync();
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, world.OrgId, OrgRole.Admin, ["short-rent"]);
        var otherAdminId = await OrgTeamHttp.AddMemberAsync(factory, world.OrgId, OrgRole.Admin, ["short-rent"]);
        var otherAdminRow = await MemberRowOfAsync(otherAdminId);
        using var admin = factory.CreateAuthenticatedClient(adminId);
        using var owner = Owner(world);

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId))).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await admin.PutAsJsonAsync($"/api/orgs/me/members/{otherAdminRow}/properties", new { propertyScope = "All" }),
            HttpStatusCode.Forbidden,
            "org_owner_required");
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsJsonAsync($"/api/orgs/me/members/{otherAdminRow}/properties", new { propertyScope = "All" })).StatusCode);
    }

    // --- The member in charge of a property ------------------------------------------------------------

    [Fact]
    public async Task Responsible_OnlyAnActiveMemberWhoReachesThePropertyCanBePutInCharge()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));

        // The collaborator reaches the first property, not the second.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = world.CollaboratorId })).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/properties/{world.HiddenId}/responsible", new { userId = world.CollaboratorId }),
            HttpStatusCode.UnprocessableEntity,
            "property_responsible_invalid");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = "auth0|nobody-here" }),
            HttpStatusCode.UnprocessableEntity,
            "property_responsible_invalid");

        var granted = await JsonAsync(await owner.GetAsync($"/api/properties/{world.GrantedId}"));
        Assert.Equal(world.CollaboratorId, granted.GetProperty("responsibleUserId").GetString());

        // Nobody in charge again: the property goes back to being told to its creator.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = (string?)null })).StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await JsonAsync(await owner.GetAsync($"/api/properties/{world.GrantedId}"))).GetProperty("responsibleUserId").ValueKind);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.PutAsJsonAsync($"/api/properties/{Guid.NewGuid()}/responsible", new { userId = world.CollaboratorId })).StatusCode);
    }

    // --- The seed data the in-memory fallback lacks ----------------------------------------------------

    /// <summary>
    /// On PostgreSQL the migrations seed the contexts, roles and permissions. The in-memory fallback of the host has none (and
    /// <c>EnsureCreated</c> seeds only a store nobody has touched, which the host's start-up already did): write the seed data
    /// of the model, once, so the same tests run locally.
    /// </summary>
    private async Task EnsureSeedsAsync()
    {
        if (factory.UsesPostgreSql)
            return;

        await SeedLock.WaitAsync();
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Roles.AnyAsync())
                return;

            var model = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDesignTimeModel>().Model;
            foreach (var entityType in model.GetEntityTypes().Where(e => e.ClrType.Name is "AppContext" or "Role" or "RolePermission"))
            {
                foreach (var row in entityType.GetSeedData())
                {
                    var entity = Activator.CreateInstance(entityType.ClrType)!;
                    // Only the columns: a seed row also carries the navigation lists of the model (shared objects), and adding
                    // them would attach and change the seed of the model itself, which every other host of the process reads.
                    foreach (var (name, value) in row.Where(column => entityType.FindProperty(column.Key) is not null))
                        entityType.ClrType.GetProperty(name)!.SetValue(entity, value);
                    db.Add(entity);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            SeedLock.Release();
        }
    }
}
