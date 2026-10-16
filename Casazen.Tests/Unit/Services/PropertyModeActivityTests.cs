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
/// actor for the job; a change that fails, a refusal and a flag that is off write nothing. The service over EF InMemory with the
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

    private PropertyModeService WithLog(bool orgTeamOn = true) => _h.NewService(_h.Db, _h.ActivityLogFor(_h.Db, orgTeamOn));

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

    // ─── Switched off, and absent ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheOrgTeamFlagOff_TheLogCollectsNothing_AndTheChangeWorksAsBefore()
    {
        var property = await _h.SeedPropertyAsync();
        var due = await _h.SeedPropertyAsync(name: "In scadenza");
        var dueChange = await _h.SeedChangeAsync(due, RentalMode.Long, Today);
        var service = WithLog(orgTeamOn: false);

        var scheduled = await service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        await service.CancelAsync(property.Id, scheduled.Id, UserId);
        await service.ApplyDueAsync();

        // Programmed, cancelled and applied as before, and the log, dormant while the flag is off, collected nothing.
        Assert.Empty(await LinesAsync());
        Assert.Equal(PropertyModeChangeStatus.Cancelled, (await _h.ReloadChangeAsync(scheduled.Id)).Status);
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(due.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(dueChange.Id)).Status);
    }

    [Fact]
    public async Task WithoutALogAtAll_TheServiceStillWorks()
    {
        // A host that does not have the writer (a test, a tool): the change is what matters.
        var property = await _h.SeedPropertyAsync();

        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        Assert.Empty(await LinesAsync());
    }

    [Fact]
    public void TheEventsAreNoLongerReserved_AndTheTrustedSuppliersStillAre()
    {
        foreach (var type in new[] { OrgActivityType.PropertyModeChangeScheduled, OrgActivityType.PropertyModeChangeCancelled, OrgActivityType.PropertyModeChanged })
            Assert.False(OrgActivityCatalog.Describe(type).Reserved, type.ToString());
        foreach (var type in new[] { OrgActivityType.TrustedSupplierAdded, OrgActivityType.TrustedSupplierRemoved })
            Assert.True(OrgActivityCatalog.Describe(type).Reserved, type.ToString());
    }
}
