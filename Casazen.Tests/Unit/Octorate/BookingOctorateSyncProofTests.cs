using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the push of the CasaZen bookings to Octorate (E08/F01/US01/T03): a create whose answer was lost never ends with a second
/// reservation (data rule, D13), and the retry endpoint never touches a booking of another org (permission rule).
/// </summary>
public class BookingOctorateSyncProofTests
{
    /// <summary>
    /// PROOF TEST: data rule (D13, Octorate has no idempotency key). The create reaches Octorate but its answer is lost (timeout), and the
    /// lookup right after it fails too: the sync is <c>Failed</c> and the event is delivered again. The next delivery looks the reservation up
    /// before anything else, finds it by the booking code and records it: one reservation on Octorate, created once. A third delivery sends
    /// nothing.
    /// </summary>
    [Fact]
    public async Task BookingPush_LostAnswer_NeverDuplicatesReservation_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync(bookingCode: "K7Q2M9X4TB");
        var octorate = new List<OctorateReservation>();
        var finds = 0;
        h.Client
            .Setup(c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()))
            .Returns((OctorateBookingTarget _, OctorateReservationCreate request, CancellationToken _) =>
            {
                // Octorate creates the reservation, then the answer never arrives.
                octorate.Add(new OctorateReservation
                {
                    Id = "130219355",
                    Refer = request.ExternalReference,
                    UpdatedAt = new DateTime(2026, 11, 6, 8, 0, 5, DateTimeKind.Utc),
                });
                return Task.FromException<OctorateReservation>(new OctorateServerException(null));
            });
        h.Client
            .Setup(c => c.FindReservationByExternalReferenceAsync(OctorateSyncHarness.Accommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string reference, CancellationToken _) => ++finds == 1
                ? Task.FromException<OctorateReservation?>(new OctorateServerException(null))
                : Task.FromResult(octorate.FirstOrDefault(r => r.Refer == reference)));

        // First delivery: the answer is lost and the reservation cannot be found yet: Failed, and the dispatcher delivers the event again.
        await using (var db = h.NewDb(new RequestTenant(OctorateSyncHarness.OrgA)))
        {
            await Assert.ThrowsAsync<OctorateServerException>(
                () => h.Service(db, new RequestTenant(OctorateSyncHarness.OrgA)).PushCreateAsync(bookingId, CancellationToken.None));
        }

        var failed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(failed);
        Assert.Equal(OctorateSyncState.Failed, failed.State);
        Assert.Equal(OctorateSyncErrorCodes.Unavailable, failed.ErrorCode);
        Assert.Null((await h.ReadBookingAsync(bookingId)).OctorateReservationId);

        // Second delivery: the lookup runs before any create and finds the reservation.
        await using (var db = h.NewDb(new RequestTenant(OctorateSyncHarness.OrgA)))
        {
            var state = await h.Service(db, new RequestTenant(OctorateSyncHarness.OrgA)).PushCreateAsync(bookingId, CancellationToken.None);
            Assert.Equal(OctorateSyncState.Synced, state);
        }

        // Third delivery: the booking has its reservation, nothing is sent.
        await using (var db = h.NewDb(new RequestTenant(OctorateSyncHarness.OrgA)))
        {
            var state = await h.Service(db, new RequestTenant(OctorateSyncHarness.OrgA)).PushCreateAsync(bookingId, CancellationToken.None);
            Assert.Equal(OctorateSyncState.Synced, state);
        }

        // One reservation on Octorate, created once; the booking points to it.
        Assert.Single(octorate);
        h.Client.Verify(
            c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal("130219355", (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        var synced = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(synced);
        Assert.Equal(OctorateSyncState.Synced, synced.State);
        Assert.Null(synced.ErrorCode);
        Assert.Equal(2, synced.Attempts);
    }

    /// <summary>
    /// PROOF TEST: permission rule (tenant isolation). A host of org A asks the retry of a failed booking of org B: the endpoint answers 404 and
    /// never reaches the service, and the service itself does not find the booking through the tenant filter of org A; the sync of org B stays
    /// as it was and no event is recorded. The control: the failed booking of org A is asked again.
    /// </summary>
    [Fact]
    public async Task BookingOctorateSyncRetry_OtherOrgBooking_Is404_Test()
    {
        var h = new OctorateSyncHarness();
        var foreign = await h.SeedBookingAsync(orgId: OctorateSyncHarness.OrgB, bookingCode: "B0RGB00001");
        var mine = await h.SeedBookingAsync(bookingCode: "A0RGA00001");
        await using (var seed = h.NewDb())
        {
            seed.OctorateBookingSyncs.AddRange(FailedSync(OctorateSyncHarness.OrgB, foreign), FailedSync(OctorateSyncHarness.OrgA, mine));
            await seed.SaveChangesAsync();
        }

        var tenantA = new RequestTenant(OctorateSyncHarness.OrgA);
        await using var asOrgA = h.NewDb(tenantA);

        // The endpoint: the booking of org B is not a resource the caller of org A reaches.
        var sync = new Mock<IBookingOctorateSyncService>(MockBehavior.Strict);
        var controller = new BookingOctorateSyncController(
            sync.Object,
            new HostResourceLookup(asOrgA),
            Mock.Of<IAuthorizationService>(),
            NullLogger<BookingOctorateSyncController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var answer = await controller.Retry(foreign, overwrite: true, CancellationToken.None);
        Assert.IsType<NotFoundResult>(answer.Result);
        sync.VerifyNoOtherCalls();

        // The service: the same booking through the tenant filter of org A is not found.
        var service = h.Service(asOrgA, tenantA);
        await Assert.ThrowsAsync<NotFoundException>(() => service.RequeueAsync(foreign, overwrite: false, CancellationToken.None));

        await using (var read = h.NewDb())
        {
            var theirs = await read.OctorateBookingSyncs.AsNoTracking().SingleAsync(s => s.BookingId == foreign);
            Assert.Equal(OctorateSyncState.Failed, theirs.State);
            Assert.False(await read.OutboxEvents.AnyAsync(e => e.EntityId == foreign));
        }

        // The control: the failed booking of org A is asked again; the push is queued as its own event and the row keeps its state until
        // the push really starts.
        Assert.Equal(OctorateSyncState.Pending, await service.RequeueAsync(mine, overwrite: false, CancellationToken.None));
        await using (var read = h.NewDb())
        {
            var requested = await read.OutboxEvents.AsNoTracking().SingleAsync(e => e.EntityId == mine);
            Assert.Equal(DomainEventType.OctorateSyncRequested, requested.EventType);
            Assert.Equal(OctorateSyncHarness.OrgA, requested.OrgId);
            Assert.Equal(OctorateSyncState.Failed, (await read.OctorateBookingSyncs.AsNoTracking().SingleAsync(s => s.BookingId == mine)).State);
        }
    }

    /// <summary>
    /// PROOF TEST: data rule. A retry is queued but the push gives up (the booking was cancelled meanwhile, so nothing is sent): the row must
    /// not be left <c>Pending</c> with nothing to complete it, where the next retry would answer 409 and the host would have no way out. It
    /// keeps its failed state, the host can ask again, and a mirror that is not failed (here, synced) is still refused with the 409.
    /// </summary>
    [Fact]
    public async Task BookingOctorateSyncRetry_PushThatGivesUp_LeavesARowTheHostCanAskAgain_Test()
    {
        var h = new OctorateSyncHarness();
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        var cancelled = await h.SeedBookingAsync(bookingCode: "C0NCEL0001");
        var synced = await h.SeedBookingAsync(bookingCode: "S0SYNC0001");
        await using (var seed = h.NewDb())
        {
            var done = FailedSync(OctorateSyncHarness.OrgA, synced);
            done.State = OctorateSyncState.Synced;
            done.ErrorCode = null;
            seed.OctorateBookingSyncs.AddRange(FailedSync(OctorateSyncHarness.OrgA, cancelled), done);
            await seed.SaveChangesAsync();
        }

        // The host asks again; before the event is delivered the booking is cancelled.
        await using (var db = h.NewDb(tenant))
        {
            Assert.Equal(OctorateSyncState.Pending, await h.Service(db, tenant).RequeueAsync(cancelled, overwrite: false, CancellationToken.None));
        }

        await using (var db = h.NewDb())
        {
            (await db.Bookings.SingleAsync(b => b.Id == cancelled)).Status = BookingStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        // The push gives up without touching Octorate (the client is strict and has nothing set up for it).
        await using (var db = h.NewDb(tenant))
        {
            Assert.Null(await h.Service(db, tenant).PushCreateAsync(cancelled, CancellationToken.None));
        }

        // The row is as it was, failed, so the host is not stuck: the retry is accepted again.
        Assert.Equal(OctorateSyncState.Failed, (await h.ReadSyncAsync(cancelled))!.State);
        await using (var db = h.NewDb(tenant))
        {
            var service = h.Service(db, tenant);
            Assert.Equal(OctorateSyncState.Pending, await service.RequeueAsync(cancelled, overwrite: false, CancellationToken.None));
            await Assert.ThrowsAsync<DomainConflictException>(() => service.RequeueAsync(synced, overwrite: false, CancellationToken.None));
        }
    }

    /// <summary>
    /// PROOF TEST: data rule. The read of the target on Octorate (the PMS rooms of the unit) fails with a 5xx before any create: the booking
    /// does not stay without a trace. Its row is <c>Failed</c> with <c>octorate_unavailable</c> (visible, and retryable by the host), the
    /// exception goes up so the outbox delivers the event again, nothing is created, and the delivery that finds Octorate up again creates
    /// the reservation once.
    /// </summary>
    [Fact]
    public async Task BookingPush_TargetLookupFails_LeavesAFailedRowTheHostCanRetry_Test()
    {
        var h = new OctorateSyncHarness();
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        var bookingId = await h.SeedBookingAsync(bookingCode: "K7Q2M9X4TB");
        h.Client
            .Setup(c => c.GetPmsRoomsAsync(OctorateSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        await using (var db = h.NewDb(tenant))
        {
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Service(db, tenant).PushCreateAsync(bookingId, CancellationToken.None));
        }

        var failed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(failed);
        Assert.Equal(OctorateSyncState.Failed, failed.State);
        Assert.Equal(OctorateSyncErrorCodes.Unavailable, failed.ErrorCode);
        Assert.Null((await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        h.Client.Verify(
            c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The host can ask again from the state he sees: a failed row is retryable.
        await using (var db = h.NewDb(tenant))
        {
            Assert.Equal(OctorateSyncState.Pending, await h.Service(db, tenant).RequeueAsync(bookingId, overwrite: false, CancellationToken.None));
        }

        // Octorate answers again: the next delivery creates the reservation, once.
        h.Client
            .Setup(c => c.GetPmsRoomsAsync(OctorateSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OctoratePmsRoom(OctorateSyncHarness.PmsRoom, OctorateSyncHarness.Room, "Sky 001 Real Room") });
        h.Client
            .Setup(c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation
            {
                Id = "130219355",
                Refer = "K7Q2M9X4TB",
                UpdatedAt = new DateTime(2026, 11, 6, 8, 0, 5, DateTimeKind.Utc),
            });
        await using (var db = h.NewDb(tenant))
        {
            Assert.Equal(OctorateSyncState.Synced, await h.Service(db, tenant).PushCreateAsync(bookingId, CancellationToken.None));
        }

        h.Client.Verify(
            c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal("130219355", (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        var synced = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(synced);
        Assert.Equal(OctorateSyncState.Synced, synced.State);
        Assert.Null(synced.ErrorCode);
    }

    /// <summary>
    /// The read of the target is refused by Octorate (here a 401): the row is <c>Failed</c> with <c>octorate_rejected</c> and the push returns,
    /// because a delivery again would be refused again; the host asks again once the cause is fixed.
    /// </summary>
    [Fact]
    public async Task BookingPush_TargetLookupRefused_RecordsRejected_AndIsNotDeliveredAgain_Test()
    {
        var h = new OctorateSyncHarness();
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        var bookingId = await h.SeedBookingAsync();
        h.Client
            .Setup(c => c.GetPmsRoomsAsync(OctorateSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.Refused(401));

        await using (var db = h.NewDb(tenant))
        {
            Assert.Equal(OctorateSyncState.Failed, await h.Service(db, tenant).PushCreateAsync(bookingId, CancellationToken.None));
        }

        var failed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(failed);
        Assert.Equal(OctorateSyncState.Failed, failed.State);
        Assert.Equal(OctorateSyncErrorCodes.Rejected, failed.ErrorCode);
        h.Client.Verify(
            c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Casazen.Core.Entities.OctorateBookingSync FailedSync(Guid orgId, Guid bookingId) => new()
    {
        OrgId = orgId,
        BookingId = bookingId,
        PropertyId = Guid.NewGuid(),
        State = OctorateSyncState.Failed,
        LastOperation = OctorateSyncOperation.Create,
        Attempts = 1,
        ErrorCode = OctorateSyncErrorCodes.Rejected,
    };
}
