using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-REL: the events the reliability of a supplier is made of. Each event is written once, a failure to write one never reaches the
/// caller, a missed deadline is not held against a supplier that could not answer, and the points are what the events of the window
/// add up to. The supplier of the scenario works Monday to Friday 08-13 and 14-18, Saturday 08-14, never on Sunday; the clock is
/// Thursday 8 October 2026, 12:00 in Rome.
/// </summary>
public class SupplierReliabilityServiceTests
{
    private static readonly DateTime Now = ServiceRequestScenario.Instant.UtcDateTime;

    [Fact]
    public async Task RecordAsync_AnEventOfARequest_IsWrittenOnceForEachKind()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = Guid.NewGuid();
        var reliability = s.Kit.Reliability;

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.JobCompletedClean, request, Now);
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.JobCompletedClean, request, Now.AddHours(1));
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.RepliedInTime, request, Now);

        var events = await s.ReliabilityEventsAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Kind == SupplierReliabilityEventKind.JobCompletedClean && e.OccurredAt == Now);
        Assert.Contains(events, e => e.Kind == SupplierReliabilityEventKind.RepliedInTime);
    }

    [Fact]
    public async Task RecordAsync_TheSameEventOfAnotherSupplier_IsAnotherEvent_AndAnEventWithoutARequestIsNotMerged()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var request = Guid.NewGuid();
        var reliability = s.Kit.Reliability;

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.DisputeOpened, request, Now);
        await reliability.RecordAsync(other, SupplierReliabilityEventKind.DisputeOpened, request, Now);
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.DisputeOpened, null, Now);
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.DisputeOpened, null, Now);

        Assert.Equal(3, (await s.ReliabilityEventsAsync()).Count);
        Assert.Single(await s.ReliabilityEventsAsync(other));
    }

    [Fact]
    public async Task RecordAsync_ARefusedSave_IsSwallowed_AndNothingStaysInTheContextToBeSavedAgain()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        interceptor.Failure = new InvalidOperationException("the database is down");

        await s.Kit.Reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.JobCompletedClean, Guid.NewGuid(), Now);
        interceptor.Failure = null;

        Assert.Empty(s.Db.ChangeTracker.Entries<SupplierReliabilityEvent>());
        Assert.Empty(await s.ReliabilityEventsAsync());
        // The next save of the caller carries nothing of the lost event.
        await s.Db.SaveChangesAsync();
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task RecordReplyAsync_AnAnswerInTimeEarnsAPoint_ALateOneOrOneWithoutDeadlineEarnsNone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var inTime = Guid.NewGuid();
        var late = Guid.NewGuid();
        var noDeadline = Guid.NewGuid();
        var reliability = s.Kit.Reliability;

        // Exactly at the deadline is still in time.
        await reliability.RecordReplyAsync(s.SupplierOrgId, inTime, Now, dueAt: Now);
        await reliability.RecordReplyAsync(s.SupplierOrgId, late, Now.AddMinutes(1), dueAt: Now);
        await reliability.RecordReplyAsync(s.SupplierOrgId, noDeadline, Now, dueAt: null);

        var recorded = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, recorded.Kind);
        Assert.Equal(inTime, recorded.RequestId);
        Assert.False(recorded.AsBackup);
    }

    // ─── A missed deadline ───

    [Fact]
    public async Task RecordNoReplyAsync_ASupplierThatCouldHaveAnswered_LosesPoints_AndIsToldSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = Guid.NewGuid();

        // The request waited from 10:00 to 12:00 in Rome on a Thursday, inside the morning hours.
        var told = await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, request, Now.AddHours(-2), Now);

        Assert.True(told);
        var recorded = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, recorded.Kind);
        Assert.Equal(request, recorded.RequestId);
        Assert.False(recorded.AsBackup);
        Assert.Equal(Now, recorded.OccurredAt);
    }

    [Fact]
    public async Task RecordNoReplyAsync_ABackup_IsRecordedAsABackup_AndLosesTheSmallShare()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var told = await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, Guid.NewGuid(), Now.AddHours(-2), Now, asBackup: true);

        Assert.True(told);
        Assert.True(Assert.Single(await s.ReliabilityEventsAsync()).AsBackup);
        var points = await s.Kit.Reliability.PointsAsync([s.SupplierOrgId]);
        Assert.Equal(-2, points[s.SupplierOrgId]);
    }

    [Fact]
    public async Task RecordNoReplyAsync_TheSameMissedDeadlineTwice_CostsPointsOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = Guid.NewGuid();

        await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, request, Now.AddHours(-2), Now);
        await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, request, Now.AddHours(-2), Now);

        Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(-10, (await s.Kit.Reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
    }

    [Theory]
    [InlineData("2026-10-11T08:00:00Z", "2026-10-11T10:00:00Z")] // Sunday 10:00-12:00 in Rome: the supplier does not work on Sundays
    [InlineData("2026-10-08T18:00:00Z", "2026-10-08T20:00:00Z")] // Thursday 20:00-22:00 in Rome: after the evening hours
    [InlineData("2026-10-08T04:00:00Z", "2026-10-08T05:30:00Z")] // Thursday 06:00-07:30 in Rome: before the morning hours
    public async Task RecordNoReplyAsync_AWindowOutsideTheWorkingHours_IsExcused(string started, string due)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var told = await s.Kit.Reliability.RecordNoReplyAsync(
            s.SupplierOrgId, Guid.NewGuid(), DateTime.Parse(started).ToUniversalTime(), DateTime.Parse(due).ToUniversalTime());

        Assert.False(told);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task RecordNoReplyAsync_AWindowThatTouchesTheWorkingHours_IsNotExcused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        // Thursday 17:00-19:00 in Rome: the last hour is inside the afternoon band (14-18).
        var told = await s.Kit.Reliability.RecordNoReplyAsync(
            s.SupplierOrgId, Guid.NewGuid(), new DateTime(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 17, 0, 0, DateTimeKind.Utc));

        Assert.True(told);
        Assert.Single(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task RecordNoReplyAsync_ASupplierOnTimeOff_IsExcused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Db.SupplierTimeOff.Add(new SupplierTimeOff
        {
            OrgId = s.SupplierOrgId,
            FromDate = new DateOnly(2026, 10, 8),
            ToDate = new DateOnly(2026, 10, 9),
        });
        await s.Db.SaveChangesAsync();

        var told = await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, Guid.NewGuid(), Now.AddHours(-2), Now);

        Assert.False(told);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Theory]
    [InlineData(SupplierStatus.Suspended)]
    [InlineData(SupplierStatus.Pending)]
    public async Task RecordNoReplyAsync_ASupplierThatIsNotActive_IsExcused(SupplierStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SetSupplierStatusAsync(status);

        var told = await s.Kit.Reliability.RecordNoReplyAsync(s.SupplierOrgId, Guid.NewGuid(), Now.AddHours(-2), Now);

        Assert.False(told);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task RecordNoReplyAsync_ASupplierWhoWroteNoHours_IsTakenToHaveBeenThere()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var noHours = await s.AddOtherSupplierAsync();

        // A Sunday night: with hours it would be excused, but nothing says this supplier was away.
        var told = await s.Kit.Reliability.RecordNoReplyAsync(
            noHours, Guid.NewGuid(), new DateTime(2026, 10, 11, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc));

        Assert.True(told);
        Assert.Single(await s.ReliabilityEventsAsync(noHours));
    }

    [Fact]
    public async Task RecordNoReplyAsync_WithTheReliabilityOff_IsRecordedButTheSupplierIsNotToldItLostPoints()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var off = new SupplierReliabilityService(
            s.Db,
            s.Agenda,
            Options.Create(new SupplierRankingOptions { ReliabilityEnabled = false }),
            NullLogger<SupplierReliabilityService>.Instance,
            s.Clock);

        var told = await off.RecordNoReplyAsync(s.SupplierOrgId, Guid.NewGuid(), Now.AddHours(-2), Now);

        Assert.False(told);
        // The history is kept, so the weights can be turned on later and applied to what already happened.
        Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Empty(await off.PointsAsync([s.SupplierOrgId]));
    }

    // ─── The points ───

    [Fact]
    public async Task PointsAsync_AddsUpTheEventsOfTheWindowOfEachSupplierOnly()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var reliability = s.Kit.Reliability;

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.RepliedInTime, Guid.NewGuid(), Now.AddDays(-3));
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.RepliedInTime, Guid.NewGuid(), Now.AddDays(-30));
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.JobCompletedClean, Guid.NewGuid(), Now.AddDays(-10));
        await reliability.RecordAsync(other, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), Now.AddDays(-1));

        var points = await reliability.PointsAsync([s.SupplierOrgId, other, Guid.NewGuid()]);

        Assert.Equal(1 + 1 + 2, points[s.SupplierOrgId]);
        Assert.Equal(-10, points[other]);
        // Nobody else has a history: the supplier starts neutral (a missing entry reads as 0).
        Assert.Equal(2, points.Count);
    }

    [Fact]
    public async Task PointsAsync_WhatIsOlderThanTheWindow_NoLongerCounts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var reliability = s.Kit.Reliability;

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), Now.AddDays(-91));
        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), Now.AddDays(-89));

        Assert.Equal(-10, (await reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
        s.Clock.Advance(TimeSpan.FromDays(2));
        // Two days later the second one has left the window too: no event, no points, a neutral supplier.
        Assert.Empty(await reliability.PointsAsync([s.SupplierOrgId]));
    }

    [Fact]
    public async Task PointsAsync_ADisputeTakesBackThePointsOfTheJobAndCostsItsOwn()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var job = Guid.NewGuid();
        var reliability = s.Kit.Reliability;

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.JobCompletedClean, job, Now.AddDays(-5));
        Assert.Equal(2, (await reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);

        await reliability.RecordAsync(s.SupplierOrgId, SupplierReliabilityEventKind.DisputeOpened, job, Now.AddDays(-1));

        Assert.Equal(-12, (await reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
    }

    [Fact]
    public async Task PointsAsync_WithNoSuppliers_ReadsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        Assert.Empty(await s.Kit.Reliability.PointsAsync([]));
    }
}
