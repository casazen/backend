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
/// again where it should be; a booking whose create was tried and has no reservation id has its reservation looked up by reference; a property that
/// is not mirrored now is never written to but leaves a row the host can ask again.
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

    /// <summary>
    /// PROOF TEST: data rule (NF-76, D13). A booking cancelled while the create of its reservation had no answer (the row has attempts: a process
    /// stopped during the call, or Octorate did not answer) may have its reservation on Octorate all the same, and the create gives up on a cancelled
    /// booking, so it would never look for it. The cancellation looks the reservation up by its reference, links it to the booking and cancels it
    /// (the row ends <c>Synced</c>); nothing is cancelled on a guess, and a booking with no create tried has nothing on Octorate to look for.
    /// </summary>
    [Fact]
    public async Task Handle_ABookingWhoseCreateWasTried_HasItsReservationLookedUpByReference_LinkedAndCancelled()
    {
        foreach (var (state, errorCode) in new[] { (OctorateSyncState.Pending, (string?)null), (OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable) })
        {
            var h = new OctorateSyncHarness();
            var bookingId = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "L7Q2M9X4TB");
            await SeedCreateRowAsync(h, bookingId, state, errorCode, attempts: 1);
            GivenReservationWithReference(h, "L7Q2M9X4TB", "9301");
            h.Client
                .Setup(c => c.CancelReservationAsync(
                    It.Is<OctorateBookingTarget>(t => t.AccommodationId == OctorateSyncHarness.Accommodation && t.ProductId == OctorateSyncHarness.Room),
                    "9301",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));

            // Linked to the booking, as the create does when it hears the answer, and cancelled on Octorate once.
            Assert.Equal("9301", (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
            h.Client.Verify(
                c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9301", It.IsAny<CancellationToken>()), Times.Once);
            var sync = await h.ReadSyncAsync(bookingId);
            Assert.NotNull(sync);
            Assert.Equal(OctorateSyncState.Synced, sync.State);
            Assert.Equal(OctorateSyncOperation.Cancel, sync.LastOperation);
            Assert.Null(sync.ErrorCode);
            Assert.Equal(2, sync.Attempts);
        }
    }

    [Fact]
    public async Task Handle_ABookingWhoseCreateWasTried_WithNoReservationByItsReference_SendsNothing_AndLinksNothing()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "L7Q2M9X4TB");
        await SeedCreateRowAsync(h, bookingId, OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable, attempts: 1);
        h.Client
            .Setup(c => c.FindReservationByExternalReferenceAsync(OctorateSyncHarness.Accommodation, "L7Q2M9X4TB", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OctorateReservation?)null);

        // The whole list was read and has none: the reservation was never made, there is nothing to cancel.
        await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null((await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        var sync = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(sync);
        Assert.Equal(OctorateSyncState.Failed, sync.State);
        Assert.Equal(OctorateSyncOperation.Create, sync.LastOperation);
    }

    [Fact]
    public async Task Handle_TheLookupOfTheReservationOfTheCreateHasNoAnswer_LeavesAFailedRow_AndTheEventIsDeliveredAgain()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "L7Q2M9X4TB");
        await SeedCreateRowAsync(h, bookingId, OctorateSyncState.Pending, null, attempts: 1);
        h.Client
            .SetupSequence(c => c.FindReservationByExternalReferenceAsync(OctorateSyncHarness.Accommodation, "L7Q2M9X4TB", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(null))
            .ReturnsAsync(new OctorateReservation { Id = "9301", Refer = "L7Q2M9X4TB", Status = OctorateBookingStatus.Confirmed });
        h.Client
            .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9301", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var delivered = await EventAsync(h, bookingId, BookingEventOrigin.Host);

        // Octorate does not answer: never read as «no reservation». The row says it, the exception goes up so the event is delivered again.
        await Assert.ThrowsAsync<OctorateServerException>(() => HandleAsync(h, delivered));
        var failed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(failed);
        Assert.Equal(OctorateSyncState.Failed, failed.State);
        Assert.Equal(OctorateSyncOperation.Cancel, failed.LastOperation);
        Assert.Equal(OctorateSyncErrorCodes.Unavailable, failed.ErrorCode);
        Assert.Null((await h.ReadBookingAsync(bookingId)).OctorateReservationId);

        // The delivery that follows finds the reservation (the row still says a create was tried), links it and cancels it.
        await HandleAsync(h, delivered);
        Assert.Equal("9301", (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        Assert.Equal(OctorateSyncState.Synced, (await h.ReadSyncAsync(bookingId))!.State);
        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9301", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ABookingWhoseCreateWasTried_OnAPropertyNotMirroredNow_LeavesAFailedRow_UntilTheHostAsksAgain()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "L7Q2M9X4TB");
        await SeedCreateRowAsync(h, bookingId, OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable, attempts: 1);
        await SetConnectionAsync(h, OctorateConnectionStatus.Disconnected);

        // The property cannot be looked into: nothing is asked of Octorate, and the cancellation is not consumed with no trace.
        await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));
        var row = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(row);
        Assert.Equal(OctorateSyncState.Failed, row.State);
        Assert.Equal(OctorateSyncOperation.Cancel, row.LastOperation);
        Assert.Equal(OctorateSyncErrorCodes.NotMirrored, row.ErrorCode);

        // Back: the host asks again, the reservation of the create is found, linked and cancelled.
        await SetConnectionAsync(h, OctorateConnectionStatus.Connected);
        GivenReservationWithReference(h, "L7Q2M9X4TB", "9301");
        h.Client
            .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9301", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.System, DomainEventType.OctorateSyncRequested));

        Assert.Equal("9301", (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        Assert.Equal(OctorateSyncState.Synced, (await h.ReadSyncAsync(bookingId))!.State);
    }

    [Fact]
    public async Task Handle_ABookingWithNoReservationAndNoCreateTried_HasNothingOnOctorateToCancel_AndNothingIsAsked()
    {
        var h = new OctorateSyncHarness();
        var withNoRow = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "N7Q2M9X4TB");
        var neverSent = await SeedCancelledAsync(h, reservationId: null, withRow: false, bookingCode: "O7Q2M9X4TB");

        // A row with no attempt: the create stopped before it called Octorate (the room was not chosen).
        await SeedCreateRowAsync(h, neverSent, OctorateSyncState.Failed, OctorateSyncErrorCodes.RoomNotChosen, attempts: 0);

        // The strict client has no lookup and no cancellation set up: any call fails the test.
        await HandleAsync(h, await EventAsync(h, withNoRow, BookingEventOrigin.Host));
        await HandleAsync(h, await EventAsync(h, neverSent, BookingEventOrigin.Host));

        Assert.Null(await h.ReadSyncAsync(withNoRow));
        Assert.Equal(OctorateSyncErrorCodes.RoomNotChosen, (await h.ReadSyncAsync(neverSent))!.ErrorCode);
        Assert.Equal(OctorateSyncOperation.Create, (await h.ReadSyncAsync(neverSent))!.LastOperation);
        h.Client.Verify(
            c => c.FindReservationByExternalReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void GivenReservationWithReference(OctorateSyncHarness h, string reference, string reservationId) =>
        h.Client
            .Setup(c => c.FindReservationByExternalReferenceAsync(OctorateSyncHarness.Accommodation, reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = reservationId, Refer = reference, Status = OctorateBookingStatus.Confirmed });

    /// <summary>The row the create left on a booking: it called Octorate <paramref name="attempts"/> times and ended in <paramref name="state"/>.</summary>
    private static async Task SeedCreateRowAsync(OctorateSyncHarness h, Guid bookingId, OctorateSyncState state, string? errorCode, int attempts)
    {
        await using var seed = h.NewDb();
        seed.OctorateBookingSyncs.Add(new OctorateBookingSync
        {
            OrgId = OrgA,
            BookingId = bookingId,
            PropertyId = h.PropertyId,
            State = state,
            ErrorCode = errorCode,
            LastOperation = OctorateSyncOperation.Create,
            Attempts = attempts,
        });
        await seed.SaveChangesAsync();
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

    /// <summary>
    /// PROOF TEST: data rule (NF-76, OCT-04). A booking that has its reservation on Octorate and whose property is not mirrored now (the connection was
    /// lost, the mapping is not confirmed or still to decide, so write-blocked) is never written to, but its cancellation is not consumed with no trace:
    /// the row is <c>Failed</c> with <c>octorate_not_mirrored</c> (nothing was sent), so the host can ask again, and the retry sends it, once, when the
    /// property is mirrored again.
    /// </summary>
    [Fact]
    public async Task Handle_APropertyNotMirroredNow_LeavesAFailedRow_AndNothingIsSent_UntilTheHostAsksAgain()
    {
        var cases = new (Func<OctorateSyncHarness, Task> Break, Func<OctorateSyncHarness, Task> Restore)[]
        {
            // The refresh token was refused: no call is made for the org until the host connects again.
            (harness => SetConnectionAsync(harness, OctorateConnectionStatus.Disconnected), harness => SetConnectionAsync(harness, OctorateConnectionStatus.Connected)),

            // A mapping that is not confirmed any more: the property has no listing to write to.
            (harness => SetMappingAsync(harness, OctorateMappingStatus.Rejected), harness => SetMappingAsync(harness, OctorateMappingStatus.Confirmed)),

            // A new proposal for the same property, still to decide: writes are blocked until a person confirms.
            (AddProposedMappingAsync, RemoveProposedMappingsAsync),
        };

        foreach (var (breakMirror, restoreMirror) in cases)
        {
            var h = new OctorateSyncHarness();
            var bookingId = await SeedCancelledAsync(h);
            await breakMirror(h);

            // The cancellation arrives while the property is not mirrored: nothing leaves, the row says so.
            await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.Host));
            await AssertNotSentAsync(h, bookingId);

            // The host asks again before the connection or the mapping is back: the same row, nothing leaves.
            await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.System, DomainEventType.OctorateSyncRequested));
            await AssertNotSentAsync(h, bookingId);

            // Back: the host asks again and the cancellation is sent, once.
            await restoreMirror(h);
            h.Client
                .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            await HandleAsync(h, await EventAsync(h, bookingId, BookingEventOrigin.System, DomainEventType.OctorateSyncRequested));

            h.Client.Verify(
                c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "9001", It.IsAny<CancellationToken>()), Times.Once);
            var synced = await h.ReadSyncAsync(bookingId);
            Assert.NotNull(synced);
            Assert.Equal(OctorateSyncState.Synced, synced.State);
            Assert.Equal(OctorateSyncOperation.Cancel, synced.LastOperation);
            Assert.Null(synced.ErrorCode);
        }
    }

    private static async Task AssertNotSentAsync(OctorateSyncHarness h, Guid bookingId)
    {
        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var row = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(row);
        Assert.Equal(OctorateSyncState.Failed, row.State);
        Assert.Equal(OctorateSyncOperation.Cancel, row.LastOperation);
        Assert.Equal(OctorateSyncErrorCodes.NotMirrored, row.ErrorCode);
        Assert.Equal(1, row.Attempts);
    }

    private static async Task SetConnectionAsync(OctorateSyncHarness h, OctorateConnectionStatus status)
    {
        await using var db = h.NewDb();
        (await db.OctorateConnections.SingleAsync()).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task SetMappingAsync(OctorateSyncHarness h, OctorateMappingStatus status)
    {
        await using var db = h.NewDb();
        (await db.OctorateListingMappings.SingleAsync()).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task AddProposedMappingAsync(OctorateSyncHarness h)
    {
        await using var db = h.NewDb();
        var connection = await db.OctorateConnections.SingleAsync();
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgA,
            ConnectionId = connection.Id,
            OctorateAccommodationId = "777001",
            SnapshotName = "Casa del Lago (altro annuncio)",
            PropertyId = h.PropertyId,
            Status = OctorateMappingStatus.Proposed,
        });
        await db.SaveChangesAsync();
    }

    private static async Task RemoveProposedMappingsAsync(OctorateSyncHarness h)
    {
        await using var db = h.NewDb();
        db.OctorateListingMappings.RemoveRange(await db.OctorateListingMappings.Where(m => m.Status == OctorateMappingStatus.Proposed).ToListAsync());
        await db.SaveChangesAsync();
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
