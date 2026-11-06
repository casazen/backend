using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The cancellation of a booking CasaZen holds, pushed to Octorate after it (E08/F02/US02/T02, NF-76, AC-NF76-1): CasaZen first, Octorate after, with a
/// state the host sees on the booking; a cancellation that came from Octorate is never sent back; a failure is on the row and the event is delivered
/// again where it should be; a cancellation that finds the create of the reservation under way waits for it.
/// </summary>
public class OctorateBookingCancelHandlerTests
{
    private static readonly Guid OrgA = OctorateSyncHarness.OrgA;

    [Fact]
    public async Task Handle_ACancelledBookingCasaZenHolds_IsCancelledOnOctorateAfterIt_AndTheRowSaysSo()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedCancelledAsync(h);
        h.Client
            .Setup(c => c.CancelReservationAsync(
                It.Is<OctorateBookingTarget>(t => t.AccommodationId == OctorateSyncHarness.Accommodation && t.ProductId == OctorateSyncHarness.Room),
                "9001",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()), Times.Once);
        var sync = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(sync);
        Assert.Equal(OctorateSyncState.Synced, sync.State);
        Assert.Equal(OctorateSyncOperation.Cancel, sync.LastOperation);
        Assert.Null(sync.ErrorCode);
        Assert.Equal(2, sync.Attempts);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, sync.LastSyncedAt);
    }

    [Fact]
    public async Task Handle_ACancellationThatCameFromOctorate_ABookingOfAChannel_AndAConnectorThatIsOff_SendNothing()
    {
        var h = new OctorateSyncHarness();
        var fromOctorate = await SeedCancelledAsync(h);
        var ofAChannel = await SeedCancelledAsync(h, BookingSource.Airbnb, bookingCode: "C7Q2M9X4TB");
        var notCancelled = await SeedCancelledAsync(h, bookingCode: "L7Q2M9X4TB");
        await using (var seed = h.NewDb())
        {
            (await seed.Bookings.SingleAsync(b => b.Id == notCancelled)).Status = BookingStatus.Confirmed;
            await seed.SaveChangesAsync();
        }

        // The cancellation of the import (or of a channel booking Octorate confirmed first): never sent back.
        await HandleAsync(h, await EventAsync(h, fromOctorate, BookingEventOrigin.Octorate));

        // A booking of a channel goes through the channel path, Octorate first.
        await HandleAsync(h, await EventAsync(h, ofAChannel, BookingEventOrigin.Host));

        // An old event of a booking that is not cancelled any more (a hold confirmed again): nothing to cancel.
        await HandleAsync(h, await EventAsync(h, notCancelled, BookingEventOrigin.System));

        // The connector is off: nothing is sent, nothing is written.
        await HandleAsync(h, await EventAsync(h, fromOctorate, BookingEventOrigin.Host), connector: false);

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(OctorateSyncState.Synced, (await h.ReadSyncAsync(fromOctorate))!.State);
        Assert.Equal(OctorateSyncOperation.Create, (await h.ReadSyncAsync(fromOctorate))!.LastOperation);
    }

    [Fact]
    public async Task Handle_NoAnswerFromOctorate_LeavesAFailedRow_AndTheEventIsDeliveredAgainUntilItWorks()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedCancelledAsync(h);
        h.Client
            .SetupSequence(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(null))
            .Returns(Task.CompletedTask);
        var delivered = await EventAsync(h, bookingId, BookingEventOrigin.Guest);

        // No answer: the row says it, the exception goes up so the dispatcher delivers the event again. The cancellation of the booking stands.
        await Assert.ThrowsAsync<OctorateServerException>(() => HandleAsync(h, delivered));
        var failed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(failed);
        Assert.Equal(OctorateSyncState.Failed, failed.State);
        Assert.Equal(OctorateSyncOperation.Cancel, failed.LastOperation);
        Assert.Equal(OctorateSyncErrorCodes.Unavailable, failed.ErrorCode);
        Assert.Equal(BookingStatus.Cancelled, (await h.ReadBookingAsync(bookingId)).Status);

        // The delivery that follows works: the row is synced.
        await HandleAsync(h, delivered);
        var synced = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(synced);
        Assert.Equal(OctorateSyncState.Synced, synced.State);
        Assert.Null(synced.ErrorCode);
        Assert.Equal(3, synced.Attempts);
        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_OctorateRefuses_IsAFailedOrConflictRow_AndIsNotRetriedByItself()
    {
        foreach (var (error, state, code) in new (Exception, OctorateSyncState, string)[]
                 {
                     (new OctorateRequestRejectedException(400), OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected),
                     (new OctorateNotFoundException(), OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected),
                     (new OctorateConflictException(), OctorateSyncState.Conflict, OctorateSyncErrorCodes.ReservationChanged),
                 })
        {
            var h = new OctorateSyncHarness();
            var bookingId = await SeedCancelledAsync(h);
            h.Client
                .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()))
                .ThrowsAsync(error);

            // Handled: no exception, so the event is consumed; the host sees the row and asks again.
            await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));

            var sync = await h.ReadSyncAsync(bookingId);
            Assert.NotNull(sync);
            Assert.Equal(state, sync.State);
            Assert.Equal(code, sync.ErrorCode);
            Assert.Equal(OctorateSyncOperation.Cancel, sync.LastOperation);
        }
    }

    [Fact]
    public async Task Handle_ACancellationThatFindsTheCreateUnderWay_WaitsForIt_AndOneWithNothingToCancelLeaves()
    {
        var h = new OctorateSyncHarness();
        var underWay = await SeedCancelledAsync(h, reservationId: null, withRow: false);
        var neverMade = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "N7Q2M9X4TB");
        await using (var seed = h.NewDb())
        {
            seed.OctorateBookingSyncs.Add(new OctorateBookingSync
            {
                OrgId = OrgA,
                BookingId = underWay,
                PropertyId = h.PropertyId,
                State = OctorateSyncState.Pending,
                LastOperation = OctorateSyncOperation.Create,
                Attempts = 1,
            });
            await seed.SaveChangesAsync();
        }

        // The reservation is being created (or a process stopped during it): the event is delivered again after it, never consumed with no effect.
        await Assert.ThrowsAsync<OctorateSyncPendingException>(
            async () => await HandleAsync(h, await EventAsync(h, underWay, BookingEventOrigin.Host)));

        // No reservation and no create under way: there is nothing on Octorate to cancel.
        await HandleAsync(h, await EventAsync(h, neverMade, BookingEventOrigin.Host));

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(await h.ReadSyncAsync(neverMade));
    }

    [Fact]
    public async Task Handle_TheHostAsksAgain_RepeatsOnlyACancellationThatWasSent()
    {
        var h = new OctorateSyncHarness();
        var failedCancel = await SeedCancelledAsync(h);
        var failedCreate = await SeedCancelledAsync(h, bookingCode: "F7Q2M9X4TB", reservationId: "9002");
        await using (var seed = h.NewDb())
        {
            var cancel = await seed.OctorateBookingSyncs.SingleAsync(s => s.BookingId == failedCancel);
            cancel.State = OctorateSyncState.Failed;
            cancel.LastOperation = OctorateSyncOperation.Cancel;
            cancel.ErrorCode = OctorateSyncErrorCodes.Rejected;
            var create = await seed.OctorateBookingSyncs.SingleAsync(s => s.BookingId == failedCreate);
            create.State = OctorateSyncState.Failed;
            create.LastOperation = OctorateSyncOperation.Create;
            create.ErrorCode = OctorateSyncErrorCodes.Unavailable;
            await seed.SaveChangesAsync();
        }

        h.Client
            .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await HandleAsync(h, await EventAsync(h, failedCancel, BookingEventOrigin.System, DomainEventType.OctorateSyncRequested));
        await HandleAsync(h, await EventAsync(h, failedCreate, BookingEventOrigin.System, DomainEventType.OctorateSyncRequested));

        // The failed cancellation was sent again and is synced; the row of a failed create is the business of the create, not of this handler.
        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(OctorateSyncState.Synced, (await h.ReadSyncAsync(failedCancel))!.State);
        Assert.Equal(OctorateSyncState.Failed, (await h.ReadSyncAsync(failedCreate))!.State);
        Assert.Equal(OctorateSyncOperation.Create, (await h.ReadSyncAsync(failedCreate))!.LastOperation);
    }

    private static async Task HandleAsync(OctorateSyncHarness h, DomainEvent domainEvent, bool connector = true)
    {
        var tenant = new RequestTenant(OrgA);
        await using var db = h.NewDb(tenant);
        var handler = new OctorateBookingCancelHandler(
            db,
            h.Client.Object,
            new OctorateOrgScope(tenant),
            h.Targets(db, tenant),
            Flags(connector),
            h.Clock,
            NullLogger<OctorateBookingCancelHandler>.Instance);
        await handler.HandleAsync(domainEvent, CancellationToken.None);
    }

    private static IFeatureFlags Flags(bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        return flags.Object;
    }

    /// <summary>The event the outbox delivers: ids, enums and dates only, as <c>BookingEvents</c> writes it.</summary>
    private static async Task<DomainEvent> EventAsync(
        OctorateSyncHarness h,
        Guid bookingId,
        BookingEventOrigin origin,
        DomainEventType type = DomainEventType.BookingCancelled)
    {
        var booking = await h.ReadBookingAsync(bookingId);
        return new DomainEvent(
            Guid.NewGuid(),
            booking.OrgId,
            type,
            bookingId,
            h.Clock.GetUtcNow().UtcDateTime,
            JsonSerializer.Serialize(BookingEventPayload.Of(booking, origin)));
    }

    /// <summary>
    /// A booking cancelled in CasaZen, of the org, mirrored on Octorate (reservation 9001) with the row of its create synced; without the
    /// reservation or the row when the test says so.
    /// </summary>
    private static async Task<Guid> SeedCancelledAsync(
        OctorateSyncHarness h,
        BookingSource source = BookingSource.Manual,
        string? reservationId = "9001",
        bool withRow = true,
        string bookingCode = "M7Q2M9X4TB")
    {
        var id = await h.SeedBookingAsync(source: source, bookingCode: bookingCode);
        await using var db = h.NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == id);
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = h.Clock.GetUtcNow().UtcDateTime;
        booking.OctorateReservationId = reservationId;
        if (withRow && reservationId is not null)
        {
            db.OctorateBookingSyncs.Add(new OctorateBookingSync
            {
                OrgId = OrgA,
                BookingId = id,
                PropertyId = h.PropertyId,
                State = OctorateSyncState.Synced,
                LastOperation = OctorateSyncOperation.Create,
                Attempts = 1,
                LastSyncedAt = h.Clock.GetUtcNow().UtcDateTime.AddDays(-2),
            });
        }

        await db.SaveChangesAsync();
        return id;
    }
}
