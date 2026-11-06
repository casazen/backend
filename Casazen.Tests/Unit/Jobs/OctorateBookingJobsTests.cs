using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// The jobs of the bookings of the channel manager (E02/F02/US01/T01 and T02): the daily import and the reconciliation read the properties of
/// the orgs that are connected, from the last complete read minus two days, one property after the other without one stopping the rest; the
/// reconciliation leaves alone what was changed in CasaZen a moment ago (ADR-031-R9); the arrival and the departure of a stay reach Octorate
/// through a queued job and never through the request of the host (ADR-031-R10).
/// </summary>
public class OctorateBookingJobsTests
{
    // ─── The daily import and the reconciliation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFirstRunReadsTheWholeHistory_TheNextOnesStartTwoDaysBeforeTheLastCompleteRead()
    {
        var h = new Octorate.BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var job = Sync(h, db);

        await job.ExecuteAsync(CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromHours(6));
        await job.ExecuteAsync(CancellationToken.None);

        // Never read completely: three years back. Read completely at 2026-11-06 08:00Z: the next run starts two days before it (4 November).
        h.Client.Verify(
            c => c.GetReservationsAsync(
                Accommodation,
                It.Is<OctorateReservationQuery>(q => q.Type == OctorateReservationListType.ModifiedBetween && q.From == new DateOnly(2023, 11, 6)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        h.Client.Verify(
            c => c.GetReservationsAsync(
                Accommodation,
                It.Is<OctorateReservationQuery>(q => q.From == new DateOnly(2026, 11, 4)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(1, await db.Bookings.CountAsync());
        Assert.Equal(Now.UtcDateTime.AddHours(6), await h.ReadCheckpointAsync());
    }

    [Fact]
    public async Task TheReconciliationLeavesAlone_WhatWasChangedInCasaZenWithinFiveMinutes_TheDailyImportDoesNot()
    {
        var h = new Octorate.BookingImportHarness();
        await h.SeedBookingAsync(
            "7001",
            source: BookingSource.Airbnb,
            status: BookingStatus.Confirmed,
            total: 100m,
            updatedAt: Now.UtcDateTime.AddMinutes(-3),
            octorateUpdatedAt: FirstVersion.AddDays(-1));
        h.Serve(Res("7001", OctorateBookingStatus.Cancelled) with { UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        // Three minutes after the person's change: protected.
        await Sync(h, db).ExecuteAsync(CancellationToken.None);
        Assert.Equal(BookingStatus.Confirmed, (await h.ReadBookingAsync("7001")).Status);

        // Ten minutes later it is not any more: the reservation cancelled on Octorate is applied.
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await Sync(h, db).ExecuteAsync(CancellationToken.None);
        Assert.Equal(BookingStatus.Cancelled, (await h.ReadBookingAsync("7001")).Status);

        // The daily import has no such window.
        var other = new Octorate.BookingImportHarness();
        await other.SeedBookingAsync(
            "7001", source: BookingSource.Airbnb, status: BookingStatus.Confirmed, total: 100m, updatedAt: Now.UtcDateTime.AddMinutes(-3), octorateUpdatedAt: FirstVersion.AddDays(-1));
        other.Serve(Res("7001", OctorateBookingStatus.Cancelled) with { UpdatedAt = SecondVersion });
        await using var otherDb = other.NewDb();
        await Import(other, otherDb).ExecuteAsync(CancellationToken.None);
        Assert.Equal(BookingStatus.Cancelled, (await other.ReadBookingAsync("7001")).Status);
    }

    [Fact]
    public async Task OnlyTheOrgsThatAreConnected_AndTheListingsThatAreConfirmedWithTheirRoom_AreRead()
    {
        var disconnected = new Octorate.BookingImportHarness();
        disconnected.Serve(Res("7001"));
        await using (var db = disconnected.NewDb())
        {
            var connection = await db.OctorateConnections.SingleAsync();
            connection.Status = OctorateConnectionStatus.Disconnected;
            await db.SaveChangesAsync();
        }

        var noRoom = new Octorate.BookingImportHarness(room: null);
        noRoom.Serve(Res("7001"));
        var proposed = new Octorate.BookingImportHarness(status: OctorateMappingStatus.Proposed);
        proposed.Serve(Res("7001"));

        foreach (var h in new[] { disconnected, noRoom, proposed })
        {
            await using var db = h.NewDb();
            await Sync(h, db).ExecuteAsync(CancellationToken.None);
            await Import(h, db).ExecuteAsync(CancellationToken.None);
            h.Client.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task APropertyThatFails_DoesNotStopTheOthers_ButAnOrgWhoseConnectionIsRefusedStopsTheOrg()
    {
        var h = new Octorate.BookingImportHarness();
        await h.AddListingAsync("111111", "222222");
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        // "111111" is read first (by accommodation id): its list fails. The other property is read all the same.
        h.Client
            .Setup(c => c.GetReservationsAsync("111111", It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        await Sync(h, db).ExecuteAsync(CancellationToken.None);
        Assert.Equal(["7001"], await db.Bookings.Select(b => b.OctorateReservationId!).ToListAsync());

        // The same org, but now Octorate refuses its connection: the rest of the org is left for the next run.
        var refused = new Octorate.BookingImportHarness();
        await refused.AddListingAsync("111111", "222222");
        refused.Serve(Res("7001"));
        refused.Client
            .Setup(c => c.GetReservationsAsync("111111", It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.Refused(403));
        await using var refusedDb = refused.NewDb();
        await Import(refused, refusedDb).ExecuteAsync(CancellationToken.None);
        refused.Client.Verify(
            c => c.GetReservationsAsync(Accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await refusedDb.Bookings.ToListAsync());
    }

    // ─── The arrival and the departure ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DomainEventType.CheckInCompleted, BookingStatus.CheckedIn)]
    [InlineData(DomainEventType.CheckOutCompleted, BookingStatus.CheckedOut)]
    public async Task TheArrivalAndTheDeparture_OfAMirroredBooking_QueueTheJobThatSendsThem(DomainEventType type, BookingStatus expected)
    {
        var h = new Octorate.BookingImportHarness();
        var bookingId = await h.SeedBookingAsync("7001", source: BookingSource.Direct, status: expected, total: 100m);
        var queue = new RecordingJobClient();
        await using var db = h.NewDb();
        var handler = PushHandler(db, queue);

        await handler.HandleAsync(Event(type, bookingId), CancellationToken.None);
        // Delivered again (at least once): another push of the same state, which Octorate takes as unchanged.
        await handler.HandleAsync(Event(type, bookingId), CancellationToken.None);

        Assert.Equal(2, queue.Jobs.Count);
        var job = queue.Jobs[0];
        Assert.Equal(typeof(OctorateBookingStatePushJob), job.Type);
        Assert.Equal(nameof(OctorateBookingStatePushJob.PushAsync), job.Method.Name);
        Assert.Equal(OrgA, job.Args[0]);
        Assert.Equal(bookingId, job.Args[1]);
        Assert.Equal(expected, job.Args[2]);
    }

    [Fact]
    public async Task NothingIsQueued_ForABookingThatIsNotMirrored_ForOtherEvents_OrWithTheConnectorOff()
    {
        var h = new Octorate.BookingImportHarness();
        var local = await h.SeedBookingAsync(null, source: BookingSource.Manual, status: BookingStatus.CheckedOut, total: 100m);
        var mirrored = await h.SeedBookingAsync("7001", source: BookingSource.Direct, status: BookingStatus.CheckedOut, total: 100m);
        var otherOrg = await h.SeedBookingAsync("7002", orgId: OrgB, propertyId: Guid.NewGuid(), source: BookingSource.Direct, status: BookingStatus.CheckedOut, total: 100m);
        var queue = new RecordingJobClient();
        await using var db = h.NewDb();

        // A booking that exists only in CasaZen; the events of the import itself (Origin Octorate), which must not echo; the connector off;
        // a booking of another org named by an event of this one.
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.CheckOutCompleted, local), CancellationToken.None);
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.BookingCancelled, mirrored), CancellationToken.None);
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.BookingConfirmed, mirrored), CancellationToken.None);
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.BookingModified, mirrored), CancellationToken.None);
        await PushHandler(db, queue, connector: false).HandleAsync(Event(DomainEventType.CheckOutCompleted, mirrored), CancellationToken.None);
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.CheckOutCompleted, otherOrg), CancellationToken.None);
        await PushHandler(db, queue).HandleAsync(Event(DomainEventType.CheckOutCompleted, Guid.Empty), CancellationToken.None);

        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task ThePushJob_RunsForTheOrgOfTheBooking_AndSendsTheState()
    {
        var seenOrg = Guid.Empty;
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var import = new Mock<IBookingImportService>();
        import
            .Setup(i => i.SyncBookingStateAsync(It.IsAny<Guid>(), It.IsAny<BookingStatus>(), It.IsAny<CancellationToken>()))
            .Callback(() => seenOrg = scope.OrgId)
            .Returns(Task.CompletedTask);
        var bookingId = Guid.NewGuid();

        await new OctorateBookingStatePushJob(import.Object, scope, NullLogger<OctorateBookingStatePushJob>.Instance)
            .PushAsync(OrgA, bookingId, BookingStatus.CheckedOut, CancellationToken.None);

        // The client takes no org: the job opened the scope of the booking's org for the call, and closed it after.
        Assert.Equal(OrgA, seenOrg);
        import.Verify(i => i.SyncBookingStateAsync(bookingId, BookingStatus.CheckedOut, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Throws<InvalidOperationException>(() => scope.OrgId);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static OctorateBookingSyncJob Sync(Octorate.BookingImportHarness h, AppDbContext db) =>
        new(db, h.Service(db), h.Scope(), StagingHost(), NullLogger<OctorateBookingSyncJob>.Instance);

    private static OctorateBookingImportJob Import(Octorate.BookingImportHarness h, AppDbContext db) =>
        new(db, h.Service(db), h.Scope(), StagingHost(), NullLogger<OctorateBookingImportJob>.Instance);

    private static IHostEnvironment StagingHost()
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(e => e.EnvironmentName).Returns("Staging");
        return host.Object;
    }

    private static OctorateBookingStatePushHandler PushHandler(AppDbContext db, IBackgroundJobClient queue, bool connector = true)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(connector);
        return new OctorateBookingStatePushHandler(db, flags.Object, queue, NullLogger<OctorateBookingStatePushHandler>.Instance);
    }

    private static DomainEvent Event(DomainEventType type, Guid entityId) =>
        new(Guid.NewGuid(), OrgA, type, entityId, Now.UtcDateTime, "{}");

    /// <summary>Keeps the jobs that are queued instead of queueing them.</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Jobs { get; } = [];

        public string Create(Job job, IState state)
        {
            Jobs.Add(job);
            return $"job-{Jobs.Count}";
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
