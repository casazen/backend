using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c (AM-02b-FU4): the activity log learns the change of mode of a property. Programming a change, cancelling it and the
/// daily job that applies it each leave one line (<c>PropertyModeChangeScheduled</c>, <c>PropertyModeChangeCancelled</c>,
/// <c>PropertyModeChanged</c>), in the transaction of the change: ids and codes only, the area of the property at that moment, no
/// actor for the job; a change that fails and a refusal write nothing. The service over EF InMemory with the
/// real log writer; the rollback that takes the line away is the transaction's, proved by <c>OrgActivityPostgresTests</c> (CI).
/// </summary>
public class PropertyModeActivityTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<List<OrgActivityEntry>> LinesAsync()
    {
        _h.Db.ChangeTracker.Clear();
        return await _h.Db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.When).ThenBy(e => e.Type).ToListAsync();
    }

    private PropertyModeService WithLog() => _h.NewService(_h.Db, _h.ActivityLogFor(_h.Db));

    // ─── Programming ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_WritesOneLine_WithWhoTheDayAndTheModesAndTheAreaTheProperyIsStillIn()
    {
        var property = await _h.SeedPropertyAsync();

        var change = await WithLog().ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var line = Assert.Single(await LinesAsync());
        Assert.Equal(OrgActivityType.PropertyModeChangeScheduled, line.Type);
        Assert.Equal((property.OrgId, UserId, property.Id.ToString()), (line.OrgId, line.ActorUserId, line.SubjectId));
        Assert.Equal(OrgActivitySubjectType.Property, line.SubjectType);
        // The property is still a short-term one on the day it is programmed: the line belongs to that area.
        Assert.Equal(OrgActivityArea.ShortRent, line.Area);
        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(("Short", "Long", "2026-10-20"), (details["fromMode"], details["toMode"], details["effectiveOn"]));
        Assert.Equal(Now.UtcDateTime, line.When);
        Assert.NotEqual(Guid.Empty, change.Id);
    }

    [Fact]
    public async Task Schedule_ALongTermPropertyGoingBackToShortStays_IsWrittenInTheLongTermArea()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);

        await WithLog().ScheduleAsync(property.Id, RentalMode.Short, Day(20), UserId);

        var line = Assert.Single(await LinesAsync());
        Assert.Equal(OrgActivityArea.LongRent, line.Area);
        Assert.Equal(("Long", "Short"), (OrgActivityDetails.Parse(line.DetailsJson)["fromMode"], OrgActivityDetails.Parse(line.DetailsJson)["toMode"]));
    }

    [Fact]
    public async Task Schedule_ARefusedChange_WritesNothing()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(18));
        var service = WithLog();

        // A stay in the way, the same mode, a day that is too early: three refusals, no line.
        await Assert.ThrowsAsync<DomainConflictException>(() => service.ScheduleAsync(property.Id, RentalMode.Long, Day(15), UserId));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.ScheduleAsync(property.Id, RentalMode.Short, Day(25), UserId));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.ScheduleAsync(property.Id, RentalMode.Long, Today, UserId));

        Assert.Empty(await LinesAsync());
    }

    [Fact]
    public async Task Schedule_AChangeAlreadyScheduled_WritesNoSecondLine()
    {
        var property = await _h.SeedPropertyAsync();
        var service = WithLog();
        await service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ScheduleAsync(property.Id, RentalMode.Long, Day(30), UserId));

        Assert.Single(await LinesAsync());
    }

    // ─── Cancelling ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_WritesOneLine_WithWhoAndTheModes_AndLeavesTheProgrammingLineAlone()
    {
        var property = await _h.SeedPropertyAsync();
        var service = WithLog();
        var change = await service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        await service.CancelAsync(property.Id, change.Id, "auth0|admin");

        var lines = await LinesAsync();
        Assert.Equal([OrgActivityType.PropertyModeChangeScheduled, OrgActivityType.PropertyModeChangeCancelled], lines.Select(l => l.Type));
        var cancelled = lines[1];
        Assert.Equal(("auth0|admin", property.Id.ToString(), OrgActivityArea.ShortRent), (cancelled.ActorUserId, cancelled.SubjectId, cancelled.Area));
        var details = OrgActivityDetails.Parse(cancelled.DetailsJson);
        Assert.Equal(("Short", "Long"), (details["fromMode"], details["toMode"]));
        // A cancellation says no day: only the programming does.
        Assert.False(details.ContainsKey("effectiveOn"));
    }

    [Fact]
    public async Task Cancel_AChangeThatIsNotScheduledAnyMore_WritesNothing()
    {
        var property = await _h.SeedPropertyAsync();
        var applied = await _h.SeedChangeAsync(property, RentalMode.Long, Today, PropertyModeChangeStatus.Applied);

        await Assert.ThrowsAsync<DomainConflictException>(() => WithLog().CancelAsync(property.Id, applied.Id, UserId));

        Assert.Empty(await LinesAsync());
    }

    // ─── The daily job ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_WritesOneLineWithNoActor_InTheAreaThePropertyIsInNow()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        var result = await WithLog().ApplyDueAsync();

        Assert.Equal(1, result.Applied);
        var line = Assert.Single(await LinesAsync());
        Assert.Equal(OrgActivityType.PropertyModeChanged, line.Type);
        Assert.Null(line.ActorUserId);
        Assert.Equal((property.OrgId, property.Id.ToString(), OrgActivitySubjectType.Property), (line.OrgId, line.SubjectId, line.SubjectType));
        // The property is a long-term one from this day on: the line is in the long-term area.
        Assert.Equal(OrgActivityArea.LongRent, line.Area);
        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(("Short", "Long"), (details["fromMode"], details["toMode"]));
        Assert.False(details.ContainsKey("effectiveOn"));
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
    }

    [Fact]
    public async Task ApplyDue_BackToShortStays_IsWrittenInTheShortTermArea()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedChangeAsync(property, RentalMode.Short, Today);

        await WithLog().ApplyDueAsync();

        Assert.Equal(OrgActivityArea.ShortRent, Assert.Single(await LinesAsync()).Area);
    }

    [Fact]
    public async Task ApplyDue_TwoRuns_AreOneLine()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);
        var service = WithLog();

        await service.ApplyDueAsync();
        await service.ApplyDueAsync();

        Assert.Single(await LinesAsync());
    }

    [Fact]
    public async Task ApplyDue_AChangeThatFails_WritesNothing()
    {
        // A stay in the way on the day: the change fails, with its reason, and the log says nothing of what did not happen.
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(8), Day(14));
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        var result = await WithLog().ApplyDueAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(PropertyModeChangeStatus.Failed, (await _h.ReloadChangeAsync(change.Id)).Status);
        Assert.Empty(await LinesAsync());
    }

    [Fact]
    public async Task ApplyDue_EveryDueChangeLeavesItsOwnLine_EachInItsOwnOrg()
    {
        var first = await _h.SeedPropertyAsync(name: "Uno");
        var second = await _h.SeedPropertyAsync(RentalMode.Long, name: "Due");
        await _h.SeedChangeAsync(first, RentalMode.Long, Today);
        await _h.SeedChangeAsync(second, RentalMode.Short, Today);

        await WithLog().ApplyDueAsync();

        var lines = await LinesAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            new[] { (first.OrgId, OrgActivityArea.LongRent), (second.OrgId, OrgActivityArea.ShortRent) }.Order(),
            lines.Select(l => (l.OrgId, l.Area)).Order());
    }

    // ─── The person in charge goes with the reach (AM-02c) ──────────────────────────────────────────

    /// <summary>
    /// A property of <paramref name="mode"/> given to <paramref name="userId"/> and with that person in charge of it: a member of
    /// the org with <paramref name="role"/> and <paramref name="scope"/>, working in <paramref name="areas"/>.
    /// </summary>
    private async Task<Property> InChargeAsync(
        RentalMode mode,
        OrgRole role,
        PropertyScope scope,
        params string[] areas)
    {
        const string userId = "auth0|anna";
        var property = await _h.SeedPropertyAsync(mode);
        _h.Db.Users.Add(new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Anna",
            LastName = "Test",
            OrgId = property.OrgId,
            IsActive = true,
        });
        _h.Db.OrgMembers.Add(new OrgMember { OrgId = property.OrgId, UserId = userId, Role = role, PropertyScope = scope, Status = OrgMemberStatus.Active });
        foreach (var area in areas)
            _h.Db.UserContextMemberships.Add(new UserContextMembership { UserId = userId, ContextKey = area, RoleId = 9 });

        _h.Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = property.OrgId, UserId = userId, PropertyId = property.Id });
        (await _h.Db.Properties.SingleAsync(p => p.Id == property.Id)).ResponsibleUserId = userId;
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();
        return property;
    }

    [Fact]
    public async Task ApplyDue_ACollaboratorSoloAlcuniWhoWorksOnlyInTheModeLeft_LosesTheResponsibility_AndTheLineSaysIt()
    {
        var property = await InChargeAsync(RentalMode.Short, OrgRole.Collaborator, PropertyScope.Selected, "short-rent");
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        await WithLog().ApplyDueAsync();

        // The property is a long-term one now and the person works in short-term rentals only: it cannot open it, so it cannot be
        // in charge of it (and the notifications of its stays no longer go to it). The grant row is untouched.
        var stored = await _h.ReloadPropertyAsync(property.Id);
        Assert.Equal(RentalMode.Long, stored.RentalMode);
        Assert.Null(stored.ResponsibleUserId);
        Assert.Single(_h.Db.PropertyMemberAccesses.IgnoreQueryFilters().Where(a => a.PropertyId == property.Id));
        var line = Assert.Single(await LinesAsync());
        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(("Short", "Long", "1"), (details["fromMode"], details["toMode"], details["released"]));
    }

    [Fact]
    public async Task ApplyDue_ACollaboratorSoloAlcuniWhoWorksOnlyInTheLongTermArea_LosesAPropertyThatGoesBackToShortStays()
    {
        var property = await InChargeAsync(RentalMode.Long, OrgRole.Collaborator, PropertyScope.Selected, "long-rent");
        await _h.SeedChangeAsync(property, RentalMode.Short, Today);

        await WithLog().ApplyDueAsync();

        Assert.Null((await _h.ReloadPropertyAsync(property.Id)).ResponsibleUserId);
        Assert.Equal("1", OrgActivityDetails.Parse(Assert.Single(await LinesAsync()).DetailsJson)["released"]);
    }

    [Theory]
    [InlineData(OrgRole.Collaborator, PropertyScope.Selected, new[] { "short-rent", "long-rent" })]
    [InlineData(OrgRole.Collaborator, PropertyScope.Selected, new[] { "long-rent" })]
    [InlineData(OrgRole.Collaborator, PropertyScope.All, new[] { "short-rent" })]
    [InlineData(OrgRole.PropertyManager, PropertyScope.All, new[] { "short-rent" })]
    [InlineData(OrgRole.Admin, PropertyScope.All, new[] { "short-rent" })]
    [InlineData(OrgRole.Collaborator, PropertyScope.Selected, new string[0])]
    public async Task ApplyDue_ANameThatStillReachesTheProperty_Stays(OrgRole role, PropertyScope scope, string[] areas)
    {
        // Both areas, the area of the new mode, every property by scope or by role, and no area known (nothing narrows the grants).
        var property = await InChargeAsync(RentalMode.Short, role, scope, areas);
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        await WithLog().ApplyDueAsync();

        Assert.Equal("auth0|anna", (await _h.ReloadPropertyAsync(property.Id)).ResponsibleUserId);
        Assert.False(OrgActivityDetails.Parse(Assert.Single(await LinesAsync()).DetailsJson).ContainsKey("released"));
    }

    [Fact]
    public async Task ApplyDue_NobodyInCharge_ReleasesNothing_AndTheLineHasNoCount()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        await WithLog().ApplyDueAsync();

        Assert.False(OrgActivityDetails.Parse(Assert.Single(await LinesAsync()).DetailsJson).ContainsKey("released"));
    }

    [Fact]
    public void Catalog_TheModeEvents_AreNoLongerReservedAndTheTrustedSuppliersStillAre()
    {
        foreach (var type in new[] { OrgActivityType.PropertyModeChangeScheduled, OrgActivityType.PropertyModeChangeCancelled, OrgActivityType.PropertyModeChanged })
            Assert.False(OrgActivityCatalog.Describe(type).Reserved, type.ToString());
        // TrustedSupplierAdded is still reserved; TrustedSupplierRemoved is now written (NF-21).
        Assert.True(OrgActivityCatalog.Describe(OrgActivityType.TrustedSupplierAdded).Reserved, "TrustedSupplierAdded");
        Assert.False(OrgActivityCatalog.Describe(OrgActivityType.TrustedSupplierRemoved).Reserved, "TrustedSupplierRemoved");
    }
}
