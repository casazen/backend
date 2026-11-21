using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E31/F02/US01/T01 (NF-131): the cockpit section <c>swissNotifications</c>, built by <see cref="ComplianceWizardService.GetSummaryAsync"/> from the one list of
/// <see cref="ISwissNotificationService"/>: one item per property and arrival day of a canton other than Ticino whose duty is <c>Required</c>, of the properties the caller reaches, the day in
/// <c>Period</c>, the deadline of the regime in <c>DueAt</c>; Ticino has its own section and is never in it.
/// </summary>
public class ComplianceWizardSwissNotificationsTests : IDisposable
{
    private const string Member = "auth0|member";
    private static readonly DateOnly Day = SwissNotificationTestWorld.Day;

    private readonly SwissNotificationTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    private ComplianceWizardService Service(bool withSwiss = true, bool trafficLight = false)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(trafficLight);
        var status = new Mock<IComplianceStatusService>();
        status.Setup(s => s.EvaluateAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>())).ReturnsAsync(ComplianceStatusResult.Empty);

        return new ComplianceWizardService(
            _world.Db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IStayLifecycleService>(),
            Mock.Of<ITouristTaxQuoteService>(),
            Mock.Of<IPropertyComplianceStatusService>(),
            NullLogger<ComplianceWizardService>.Instance,
            _world.Clock,
            status.Object,
            featureFlags: flags.Object,
            swissNotifications: withSwiss ? _world.Service() : null);
    }

    [Fact]
    public async Task Cockpit_ListsTheDaysToNotifyOfTheOtherCantons_WithTheDayInPeriodAndTheDeadlineInDueAt()
    {
        var org = _world.AddOrg();
        var schaffhausen = _world.AddProperty("SH", org, name: "Casa Rosa");
        var zurich = _world.AddProperty("ZH", org, name: "Casa Ask");
        var ticino = _world.AddProperty("TI", org, name: "Casa Lugano");
        var italian = _world.AddProperty("TI", org, name: "Casa Como", country: "IT");
        foreach (var property in new[] { zurich, ticino, italian })
            _world.AddStay(property);
        _world.AddStay(schaffhausen, Day.AddDays(-1));
        _world.AddStay(schaffhausen);

        var summary = await Service().GetSummaryAsync(new HostScope(org.Id));

        // The oldest day first; the day is the period, the deadline of Schaffhausen is 02:00 of the night after the arrival (CET: 01:00Z).
        Assert.Equal(2, summary.SwissNotifications.Count);
        Assert.Equal(["2026-11-18", "2026-11-19"], summary.SwissNotifications.Items.Select(i => i.Period));
        Assert.Equal([new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc)], summary.SwissNotifications.Items.Select(i => i.DueAt!.Value));
        Assert.All(summary.SwissNotifications.Items, item =>
        {
            Assert.Equal(ComplianceCockpitAction.NotifyGuestsAuthority, item.Action);
            Assert.Equal((schaffhausen.Id, schaffhausen.Id, (Guid?)null, "Casa Rosa"), (item.Id, item.PropertyId!.Value, item.BookingId, item.Label));
            Assert.Empty(item.Missing);
            Assert.Null(item.Light);
        });

        // Ask (Zurich), Ticino (its own section) and an Italian property are not here; the Ticino section of this service is empty (its service is not given here).
        Assert.Empty(summary.TicinoNotifications.Items);
    }

    [Fact]
    public async Task Cockpit_ACollaboratorSoloAlcuni_SeesOnlyTheDaysOfThePropertiesItWasGiven_Test()
    {
        // PROOF TEST (permission): the cockpit of a collaborator limited to some properties lists the days of those properties only; another org's caller sees nothing of this org. A scope that reaches
        // no property lists nothing, never everything.
        var org = _world.AddOrg();
        var given = _world.AddProperty("SH", org, name: "Casa Data");
        var other = _world.AddProperty("SH", org, name: "Casa Altra");
        _world.AddStay(given);
        _world.AddStay(other);
        _world.Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Member, PropertyId = given.Id });
        await _world.Db.SaveChangesAsync();
        var service = Service();

        var orgWide = await service.GetSummaryAsync(new HostScope(org.Id));
        var someProperties = await service.GetSummaryAsync(new HostScope(org.Id, GrantedToUserId: Member));
        var nothingGiven = await service.GetSummaryAsync(new HostScope(org.Id, GrantedToUserId: "auth0|nessuno"));
        var anotherOrg = await service.GetSummaryAsync(new HostScope(Guid.NewGuid()));

        Assert.Equal(2, orgWide.SwissNotifications.Count);
        Assert.Equal(given.Id, Assert.Single(someProperties.SwissNotifications.Items).PropertyId);
        Assert.Equal(1, someProperties.SwissNotifications.Count);
        Assert.Empty(nothingGiven.SwissNotifications.Items);
        Assert.Empty(anotherOrg.SwissNotifications.Items);
    }

    [Fact]
    public async Task Cockpit_TheSectionIsEmpty_WhileTheModuleIsOff_OrWhenNoServiceOfTheDaysIsGiven()
    {
        var org = _world.AddOrg();
        var property = _world.AddProperty("SH", org);
        _world.AddStay(property);

        Assert.Equal(1, (await Service().GetSummaryAsync(new HostScope(org.Id))).SwissNotifications.Count);

        _world.RegimesOn = false;
        var off = await Service().GetSummaryAsync(new HostScope(org.Id));
        Assert.Equal(0, off.SwissNotifications.Count);
        Assert.Empty(off.SwissNotifications.Items);

        _world.RegimesOn = true;
        Assert.Empty((await Service(withSwiss: false).GetSummaryAsync(new HostScope(org.Id))).SwissNotifications.Items);
    }

    [Fact]
    public async Task Cockpit_ADeclaredDay_LeavesTheSection_AndADayOfARegimeWithNoDeadlineHasNoDueAt()
    {
        var org = _world.AddOrg();
        var daily = _world.AddProperty("SH", org, name: "Casa Rosa");
        var noDeadline = _world.AddProperty("FR", org, GuestNotificationOverride.Required, name: "Casa Fribourg");
        _world.AddStay(daily);
        _world.AddStay(noDeadline);
        var before = await Service().GetSummaryAsync(new HostScope(org.Id));

        await _world.Service().DeclareDayAsync(daily.Id, Day, "auth0|anna");
        var after = await Service().GetSummaryAsync(new HostScope(org.Id));

        Assert.Equal(2, before.SwissNotifications.Count);
        var left = Assert.Single(after.SwissNotifications.Items);
        Assert.Equal((noDeadline.Id, (DateTime?)null), (left.PropertyId, left.DueAt));
    }

    [Fact]
    public async Task Cockpit_WithTheTrafficLightOn_TheDayIsAnOpenItemTheHostActsOn_NeverGreen()
    {
        var org = _world.AddOrg();
        var property = _world.AddProperty("SH", org);
        _world.AddStay(property);

        var summary = await Service(trafficLight: true).GetSummaryAsync(new HostScope(org.Id));

        var item = Assert.Single(summary.SwissNotifications.Items);
        Assert.Equal(ComplianceLight.Red, item.Light);
        Assert.Equal(ComplianceReasonCodes.ActionRequired, item.Reason);
        Assert.Equal("2026-11-19", item.Period);
        Assert.Null(item.EvidenceSource);
    }
}
