using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03b: a person who loses the access to a property stops being in charge of it. The owner narrows the properties of a
/// collaborator ("Solo alcuni") or removes the person from the org, and the properties the person was in charge of go back to
/// nobody (the state of a property nobody was put in charge of: the creator stands in while it reaches the property, the owner and
/// the administrators are always told), in the same save as the grants that were taken away. What does not end the
/// responsibility: widening the access, a change to a role that reaches every property, a deactivation. The real services over EF
/// InMemory; the lock and the race with an appointment are proved on PostgreSQL (<c>OrgScopeHardeningPostgresTests</c>, CI).
/// </summary>
public class PropertyResponsibilityReleaseTests
{
    private const string OwnerId = "auth0|owner";
    private const string Anna = "auth0|anna";

    private readonly OrgInvitationTestKit _kit = new();

    private OrgPropertyAccessService Access(AppDbContext db) =>
        new(db, _kit.Cache.Object, NullLogger<OrgPropertyAccessService>.Instance, _kit.Clock);

    private sealed record World(Guid OrgId, Guid AnnaMemberId, Guid A, Guid B);

    /// <summary>An org with its owner, the collaborator Anna (every property, as a new member is) and two properties.</summary>
    private async Task<World> SeedAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        await using var db = _kit.NewDb();
        var a = HostScopeScenario.NewProperty(org.Id, OwnerId, "Trullo");
        var b = HostScopeScenario.NewProperty(org.Id, OwnerId, "Casa Bianca");
        db.Properties.AddRange(a, b);
        await db.SaveChangesAsync();
        return new World(org.Id, member.Id, a.Id, b.Id);
    }

    private async Task SetAsync(World world, PropertyScope scope, params Guid[] propertyIds)
    {
        await using var db = _kit.NewDb();
        await Access(db).SetAsync(world.OrgId, world.AnnaMemberId, OwnerId, scope, propertyIds);
    }

    private async Task PutInChargeAsync(World world, Guid propertyId, string? userId)
    {
        await using var db = _kit.NewDb();
        await Access(db).SetResponsibleAsync(world.OrgId, propertyId, userId);
    }

    private async Task<string?> ResponsibleAsync(Guid propertyId)
    {
        await using var db = _kit.NewDb();
        return (await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId)).ResponsibleUserId;
    }

    // --- The owner takes properties away ---------------------------------------------------------------

    [Fact]
    public async Task SetAsync_TakingAPropertyAway_ReleasesTheResponsibilityForThatPropertyOnly()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, Anna);
        await PutInChargeAsync(world, world.B, Anna);

        await SetAsync(world, PropertyScope.Selected, world.B);

        Assert.Null(await ResponsibleAsync(world.A));
        Assert.Equal(Anna, await ResponsibleAsync(world.B));
        await using var db = _kit.NewDb();
        var released = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == world.A);
        Assert.Equal(_kit.Now, released.UpdatedAt);
    }

    [Fact]
    public async Task SetAsync_TakingEverythingAway_ReleasesEveryResponsibilityOfThePerson()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, Anna);
        await PutInChargeAsync(world, world.B, Anna);

        await SetAsync(world, PropertyScope.Selected);

        Assert.Null(await ResponsibleAsync(world.A));
        Assert.Null(await ResponsibleAsync(world.B));
    }

    [Fact]
    public async Task SetAsync_FromEveryPropertyToSome_ReleasesTheOnesLeftOut()
    {
        // A new collaborator reaches every property, so it can be put in charge of any; narrowing it then takes some away.
        var world = await SeedAsync();
        await PutInChargeAsync(world, world.A, Anna);
        await PutInChargeAsync(world, world.B, Anna);

        await SetAsync(world, PropertyScope.Selected, world.B);

        Assert.Null(await ResponsibleAsync(world.A));
        Assert.Equal(Anna, await ResponsibleAsync(world.B));
    }

    [Fact]
    public async Task SetAsync_ChangingTheSetButKeepingTheProperty_KeepsTheResponsibility()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A);
        await PutInChargeAsync(world, world.A, Anna);

        await SetAsync(world, PropertyScope.Selected, world.A, world.B);

        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }

    [Fact]
    public async Task SetAsync_WideningToEveryProperty_KeepsTheResponsibility()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A);
        await PutInChargeAsync(world, world.A, Anna);

        await SetAsync(world, PropertyScope.All);

        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }

    [Fact]
    public async Task SetAsync_DoesNotTouchWhoElseIsInCharge()
    {
        var world = await SeedAsync();
        await _kit.SeedMemberAsync(world.OrgId, "auth0|manager", OrgRole.PropertyManager);
        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, "auth0|manager");
        await PutInChargeAsync(world, world.B, Anna);

        await SetAsync(world, PropertyScope.Selected);

        // Anna lost B; the manager, who reaches everything, is still in charge of A.
        Assert.Equal("auth0|manager", await ResponsibleAsync(world.A));
        Assert.Null(await ResponsibleAsync(world.B));
    }

    [Fact]
    public async Task SetAsync_NeverTouchesAPropertyOfAnotherOrg_EvenOneThatNamesTheSamePerson()
    {
        var world = await SeedAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        Guid foreignId;
        await using (var db = _kit.NewDb())
        {
            var foreign = HostScopeScenario.NewProperty(other.Id, "auth0|other-owner", "Altrui");
            foreign.ResponsibleUserId = Anna;
            db.Properties.Add(foreign);
            await db.SaveChangesAsync();
            foreignId = foreign.Id;
        }

        await SetAsync(world, PropertyScope.Selected);

        Assert.Equal(Anna, await ResponsibleAsync(foreignId));
    }

    [Fact]
    public async Task SetAsync_ASoftDeletedProperty_AlsoLosesItsPersonInCharge()
    {
        // A deleted property can be restored: it must not come back with a person who has lost it in the meantime.
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, Anna);
        await using (var db = _kit.NewDb())
        {
            var property = await db.Properties.SingleAsync(p => p.Id == world.A);
            property.IsDeleted = true;
            property.DeletedAt = _kit.Now;
            await db.SaveChangesAsync();
        }

        await SetAsync(world, PropertyScope.Selected, world.B);

        Assert.Null(await ResponsibleAsync(world.A));
    }

    [Fact]
    public async Task SetAsync_RefusedForAnUnknownProperty_ReleasesNothing()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, Anna);

        await Assert.ThrowsAsync<DomainRuleException>(() => SetAsync(world, PropertyScope.Selected, world.B, Guid.NewGuid()));

        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }

    // --- The person leaves, changes role or is suspended -----------------------------------------------

    [Fact]
    public async Task RemoveAsync_ReleasesEveryResponsibilityOfTheMember_AndNoOneElses()
    {
        var world = await SeedAsync();
        await _kit.SeedMemberAsync(world.OrgId, "auth0|manager", OrgRole.PropertyManager);
        await PutInChargeAsync(world, world.A, Anna);
        await PutInChargeAsync(world, world.B, "auth0|manager");

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).RemoveAsync(Anna);

        Assert.Null(await ResponsibleAsync(world.A));
        Assert.Equal("auth0|manager", await ResponsibleAsync(world.B));
        Assert.Null(await _kit.ReadMemberAsync(Anna));
    }

    [Fact]
    public async Task RemoveAsync_APersonWhoIsInvitedAgainLater_IsNotInChargeAgain()
    {
        var world = await SeedAsync();
        await PutInChargeAsync(world, world.A, Anna);
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).RemoveAsync(Anna);

        // Back in the same org as a collaborator with every property: the name was not waiting for it.
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).AddMemberAsync(Anna, world.OrgId, OrgRole.Collaborator, ["short-rent"], OwnerId);

        Assert.Null(await ResponsibleAsync(world.A));
    }

    [Fact]
    public async Task ChangeRoleAsync_ToARoleThatReachesEveryProperty_KeepsTheResponsibility()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A);
        await PutInChargeAsync(world, world.A, Anna);

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).ChangeRoleAsync(Anna, OrgRole.PropertyManager);

        // The grants are gone (the manager reaches everything) and so is nothing else: the manager is still in charge of A.
        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }

    [Fact]
    public async Task DeactivateAsync_KeepsTheResponsibility_ForTheReactivationToGiveBack()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A);
        await PutInChargeAsync(world, world.A, Anna);

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).DeactivateAsync(Anna);
        Assert.Equal(Anna, await ResponsibleAsync(world.A));

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).ReactivateAsync(Anna);
        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }

    // --- The appointment that follows ------------------------------------------------------------------

    [Fact]
    public async Task SetResponsibleAsync_AfterTheAccessWasTaken_IsRefusedUntilTheAccessIsGivenBack()
    {
        var world = await SeedAsync();
        await SetAsync(world, PropertyScope.Selected, world.A);
        await PutInChargeAsync(world, world.A, Anna);
        await SetAsync(world, PropertyScope.Selected, world.B);

        await Assert.ThrowsAsync<DomainRuleException>(() => PutInChargeAsync(world, world.A, Anna));
        Assert.Null(await ResponsibleAsync(world.A));

        await SetAsync(world, PropertyScope.Selected, world.A, world.B);
        await PutInChargeAsync(world, world.A, Anna);
        Assert.Equal(Anna, await ResponsibleAsync(world.A));
    }
}
