using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The edit of a booking CasaZen holds pushed to its reservation on Octorate (E08/F02/US01/T02, NF-75): the state the host sees, the conflict kept with
/// both versions and never overwritten silently, the retry and the «keep mine», and what is never sent (a change that came from Octorate, a booking of a
/// channel).
/// </summary>
public class BookingUpdatePushTests
{
    private const string ReservationId = "130219355";

    private static readonly DateTime T1 = new(2026, 11, 6, 8, 0, 5, DateTimeKind.Utc);

    // --- The proof -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (NF-75, AC-NF75-2). Octorate holds a newer version of the reservation than the one CasaZen knew (the dates and the guests
    /// were changed in the channel manager). The host's edit is refused as a conflict: both versions are kept (the one of Octorate on the row, the host's
    /// on the booking), a «To do» is raised once, and Octorate's version is untouched. A plain retry conflicts again: it is based on the version CasaZen
    /// saw, never on Octorate's. Only the explicit «keep mine» (<c>overwrite</c>), based on the version the host was shown, writes CasaZen's version, and
    /// then the conflict is over and its «To do» closed.
    /// </summary>
    [Fact]
    public async Task BookingUpdatePush_Conflict_NeverOverwritesOctorate_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedMirroredAsync(h);
        var octorate = new FakeOctorate(h, T1);
        octorate.ChangedOutside(new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 14), 3, T1.AddHours(1));
        var theirs = octorate.AsReservation();

        // The host's edit reaches the push: Octorate holds another version.
        Assert.Equal(OctorateSyncState.Conflict, await PushAsync(h, bookingId, askedAgain: false, overwrite: false));

        var conflict = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(conflict);
        Assert.Equal(OctorateSyncState.Conflict, conflict.State);
        Assert.Equal(OctorateSyncOperation.Update, conflict.LastOperation);
        Assert.Equal(OctorateSyncErrorCodes.ReservationChanged, conflict.ErrorCode);
        Assert.Equal(theirs.UpdatedAt, conflict.OctorateUpdateTime);
        Assert.Equal(
            new OctorateVersionSnapshot(new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 14), 3),
            OctorateVersionSnapshot.Parse(conflict.OctorateVersionJson));
        h.Activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.BookingSyncConflict && r.Action == ActivityAction.ReviewBooking
                                              && r.EntityType == InternalActivity.BookingEntityType && r.EntityId == bookingId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Neither version is lost: Octorate keeps its own, CasaZen its own.
        Assert.Equal(new DateOnly(2026, 12, 11), octorate.CheckIn);
        Assert.Equal(3, octorate.Guests);
        var booking = await h.ReadBookingAsync(bookingId);
        Assert.Equal(new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc), booking.CheckInDate);
        Assert.Equal(2, booking.NumberOfGuests);
        Assert.Equal(T1, booking.OctorateUpdatedAt);

        // A plain retry is based on the version CasaZen saw (T1), not on Octorate's: it conflicts again and writes nothing.
        Assert.Equal(OctorateSyncState.Conflict, await PushAsync(h, bookingId, askedAgain: true, overwrite: false));
        Assert.Equal(T1, octorate.Updates.Last().BasedOnUpdateTime);
        Assert.Equal(new DateOnly(2026, 12, 11), octorate.CheckIn);
        Assert.Equal(3, octorate.Guests);

        // «Keep mine»: based on the version the host was shown, so it writes CasaZen's version over exactly that one.
        Assert.Equal(OctorateSyncState.Synced, await PushAsync(h, bookingId, askedAgain: true, overwrite: true));
        Assert.Equal(theirs.UpdatedAt, octorate.Updates.Last().BasedOnUpdateTime);
        Assert.Equal(new DateOnly(2026, 12, 10), octorate.CheckIn);
        Assert.Equal(new DateOnly(2026, 12, 13), octorate.CheckOut);
        Assert.Equal(2, octorate.Guests);

        var synced = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(synced);
        Assert.Equal(OctorateSyncState.Synced, synced.State);
        Assert.Null(synced.ErrorCode);
        Assert.Null(synced.OctorateVersionJson);
        Assert.Equal(octorate.UpdatedAt, synced.OctorateUpdateTime);
        Assert.Equal(octorate.UpdatedAt, (await h.ReadBookingAsync(bookingId)).OctorateUpdatedAt);
    }

    // --- The push ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PushUpdate_AnEditOfAMirroredBooking_ReachesOctorate_AndTheRowIsSyncedWithTheNewUpdateTime()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedMirroredAsync(h, mutate: b =>
        {
            b.CheckInDate = new DateTime(2026, 12, 12, 0, 0, 0, DateTimeKind.Utc);
            b.CheckOutDate = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
            b.NumberOfGuests = 3;
            b.NumberOfAdults = 2;
            b.NumberOfChildren = 1;
            b.BasePrice = 450m;
            b.SpecialRequests = "Arrivo alle 22";
        });
        var octorate = new FakeOctorate(h, T1);

        Assert.Equal(OctorateSyncState.Synced, await PushAsync(h, bookingId, askedAgain: false, overwrite: false));

        var written = Assert.Single(octorate.Updates);
        Assert.Equal(new DateOnly(2026, 12, 12), written.CheckIn);
        Assert.Equal(new DateOnly(2026, 12, 15), written.CheckOut);
        Assert.Equal((3, 1, 450m, "Arrivo alle 22"), (written.Guests, written.Children, written.RoomGross, written.Notes));
        Assert.Equal(T1, written.BasedOnUpdateTime);

        var sync = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(sync);
        Assert.Equal((OctorateSyncState.Synced, OctorateSyncOperation.Update, octorate.UpdatedAt), (sync.State, sync.LastOperation, sync.OctorateUpdateTime));
        Assert.Equal(octorate.UpdatedAt, (await h.ReadBookingAsync(bookingId)).OctorateUpdatedAt);
    }

    [Fact]
    public async Task PushUpdate_TheCreateIsStillUnderWay_IsDeliveredAgain_NeverConsumedWithNoEffect()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync();
        await AddSyncAsync(h, bookingId, OctorateSyncState.Pending, OctorateSyncOperation.Create);

        await Assert.ThrowsAsync<OctorateSyncPendingException>(() => PushAsync(h, bookingId, askedAgain: false, overwrite: false));

        // A create that failed has nothing to update: the retry of the create sends the current data. No reservation call was made.
        await using (var db = h.NewDb())
        {
            var row = await db.OctorateBookingSyncs.SingleAsync();
            row.State = OctorateSyncState.Failed;
            await db.SaveChangesAsync();
        }

        Assert.Null(await PushAsync(h, bookingId, askedAgain: false, overwrite: false));
        h.Client.Verify(c => c.UpdateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<OctorateReservationUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushUpdate_ABookingThatCameFromOctorate_OrIsNotMirrored_SendsNothing()
    {
        var h = new OctorateSyncHarness();

        // A reservation that came from Octorate has no row of ours.
        var fromOctorate = await SeedMirroredAsync(h, withRow: false, reservationId: "1001");
        Assert.Null(await PushAsync(h, fromOctorate, askedAgain: false, overwrite: false));

        // A booking of a channel is the channel's.
        var channel = await SeedMirroredAsync(h, source: BookingSource.Airbnb, bookingCode: "C0000CHAN1", reservationId: "1002");
        Assert.Null(await PushAsync(h, channel, askedAgain: false, overwrite: false));

        // A booking that is not confirmed is not one CasaZen mirrors.
        var pending = await SeedMirroredAsync(h, bookingCode: "P0000PEND1", mutate: b => b.Status = BookingStatus.Pending, reservationId: "1003");
        Assert.Null(await PushAsync(h, pending, askedAgain: false, overwrite: false));

        h.Client.Verify(c => c.UpdateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<OctorateReservationUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushUpdate_AMappingThatIsNoLongerConfirmed_IsNeverWrittenTo_AndTheRowSaysSo()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedMirroredAsync(h);
        await using (var db = h.NewDb())
        {
            var mapping = await db.OctorateListingMappings.SingleAsync();
            mapping.Status = OctorateMappingStatus.Proposed;
            await db.SaveChangesAsync();
        }

        Assert.Equal(OctorateSyncState.Failed, await PushAsync(h, bookingId, askedAgain: false, overwrite: false));

        var sync = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(sync);
        Assert.Equal((OctorateSyncState.Failed, OctorateSyncOperation.Update, OctorateSyncErrorCodes.NotMirrored), (sync.State, sync.LastOperation, sync.ErrorCode));
        h.Client.Verify(c => c.UpdateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<OctorateReservationUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushUpdate_NoAnswerFromOctorate_IsFailedAndThrows_ARefusalIsFailedAndNotRetried()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedMirroredAsync(h);
        var attempts = 0;
        h.Client
            .Setup(c => c.UpdateReservationAsync(It.IsAny<OctorateBookingTarget>(), ReservationId, It.IsAny<OctorateReservationUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempts == 1
                ? Task.FromException<OctorateReservation>(new OctorateServerException(503))
                : Task.FromException<OctorateReservation>(new OctorateRequestRejectedException(400)));

        // 5xx: the row says so and the exception goes up, so the dispatcher delivers the event again.
        await Assert.ThrowsAsync<OctorateServerException>(() => PushAsync(h, bookingId, askedAgain: false, overwrite: false));
        var unavailable = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(unavailable);
        Assert.Equal((OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable), (unavailable.State, unavailable.ErrorCode));

        // Another refusal: Failed, not retried by itself, no exception.
        Assert.Equal(OctorateSyncState.Failed, await PushAsync(h, bookingId, askedAgain: false, overwrite: false));
        var rejected = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(rejected);
        Assert.Equal((OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected), (rejected.State, rejected.ErrorCode));
    }

    [Fact]
    public async Task RequeueAsync_KeepMine_TravelsWithTheEvent()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedMirroredAsync(h);
        await using (var db = h.NewDb())
        {
            var row = await db.OctorateBookingSyncs.SingleAsync();
            row.State = OctorateSyncState.Conflict;
            row.LastOperation = OctorateSyncOperation.Update;
            await db.SaveChangesAsync();
        }

        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        await using (var db = h.NewDb(tenant))
        {
            Assert.Equal(OctorateSyncState.Pending, await h.Service(db, tenant).RequeueAsync(bookingId, overwrite: true, CancellationToken.None));
            Assert.Equal(OctorateSyncState.Pending, await h.Service(db, tenant).RequeueAsync(bookingId, overwrite: false, CancellationToken.None));
        }

        await using var read = h.NewDb();
        var events = await read.OutboxEvents.AsNoTracking().Where(e => e.EventType == DomainEventType.OctorateSyncRequested).ToListAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Assert.Equal(2, events.Count);
        var keepMine = Assert.Single(events, e => JsonSerializer.Deserialize<BookingEventPayload>(e.PayloadJson, options)!.Overwrite);
        var plain = Assert.Single(events, e => !JsonSerializer.Deserialize<BookingEventPayload>(e.PayloadJson, options)!.Overwrite);

        // The flag is in the event only when the host chose it: the other events keep their shape.
        Assert.Contains("\"Overwrite\":true", keepMine.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Overwrite", plain.PayloadJson, StringComparison.Ordinal);
    }

    // --- The handler -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Handler_PushesTheEditsOfCasaZen_AndNeverAnEchoOrAChannel()
    {
        var push = new Mock<IBookingOctorateSyncService>(MockBehavior.Strict);
        push.Setup(s => s.PushUpdateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(OctorateSyncState.Synced);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var handler = new OctorateBookingUpdateHandler(push.Object, flags.Object, NullLogger<OctorateBookingUpdateHandler>.Instance);
        var bookingId = Guid.NewGuid();

        // The host's edit, a retry, and the host's «keep mine».
        await handler.HandleAsync(Event(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Host), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.OctorateSyncRequested, bookingId, BookingEventOrigin.System), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.OctorateSyncRequested, bookingId, BookingEventOrigin.System, overwrite: true), CancellationToken.None);
        push.Verify(s => s.PushUpdateAsync(bookingId, false, false, It.IsAny<CancellationToken>()), Times.Once);
        push.Verify(s => s.PushUpdateAsync(bookingId, true, false, It.IsAny<CancellationToken>()), Times.Once);
        push.Verify(s => s.PushUpdateAsync(bookingId, true, true, It.IsAny<CancellationToken>()), Times.Once);

        // A change that came from Octorate is never sent back; a booking of a channel is its channel's; another kind of event is not this handler's.
        await handler.HandleAsync(Event(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Octorate), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Host, BookingSource.Airbnb), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.BookingCancelled, bookingId, BookingEventOrigin.Host), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Host) with { PayloadJson = "not json" }, CancellationToken.None);

        // The connector off sends nothing.
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);
        await handler.HandleAsync(Event(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Host), CancellationToken.None);

        push.VerifyNoOtherCalls();
    }

    // --- The client ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void UpdateBody_CarriesTheStayTheGuestsThePriceAndTheRoom_AndNothingElse()
    {
        var target = new OctorateBookingTarget("585492", "956528", "956527");
        var update = new OctorateReservationUpdate
        {
            CheckIn = new DateOnly(2026, 12, 12),
            CheckOut = new DateOnly(2026, 12, 15),
            Guests = 3,
            Children = 1,
            RoomGross = 450.005m,
            Notes = "  Arrivo alle 22  ",
            BasedOnUpdateTime = T1,
        };

        using var body = JsonDocument.Parse(OctorateReservationRequests.UpdateBody(target, update));
        var root = body.RootElement;

        // The Rome days start at 23:00 UTC of the day before in winter; the room is the PMS room; the price is to the cent.
        Assert.Equal("2026-12-11T23:00:00+00:00[UTC]", root.GetProperty("checkin").GetString());
        Assert.Equal("2026-12-14T23:00:00+00:00[UTC]", root.GetProperty("checkout").GetString());
        Assert.Equal(3, root.GetProperty("totalGuest").GetInt32());
        Assert.Equal(1, root.GetProperty("totalChildren").GetInt32());
        Assert.Equal(450.01m, root.GetProperty("roomGross").GetDecimal());
        Assert.Equal(956527, root.GetProperty("pmsProduct").GetInt64());
        Assert.Equal("Arrivo alle 22", root.GetProperty("privateNotes").GetString());

        // Nothing of the status, the guests' data, the payments, the automatic charge, or the version (a PATCH is partial: Octorate stamps its own).
        var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["checkin", "checkout", "pmsProduct", "privateNotes", "roomGross", "totalChildren", "totalGuest"], names);

        // No notes: the member is left out, the notes of the reservation stay as they are; a stay with no night, or no guest, is not sent.
        using var noNotes = JsonDocument.Parse(OctorateReservationRequests.UpdateBody(target, update with { Notes = "  " }));
        Assert.False(noNotes.RootElement.TryGetProperty("privateNotes", out _));
        Assert.Throws<ArgumentException>(() => OctorateReservationRequests.UpdateBody(target, update with { CheckOut = update.CheckIn }));
        Assert.Throws<ArgumentException>(() => OctorateReservationRequests.UpdateBody(target, update with { Guests = 1, Children = 1 }));
    }

    [Fact]
    public async Task UpdateReservationAsync_TheVersionChangedOnOctorate_IsAConflictAndNothingIsWritten()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, Reservation(updateTime: "2026-11-06T09:00:05Z", checkIn: "2026-12-11", checkOut: "2026-12-14", guests: 3));

        using (h.Scope.Use(OctorateSyncHarness.OrgA))
        {
            await Assert.ThrowsAsync<OctorateConflictException>(
                () => h.Client.UpdateReservationAsync(Target(), ReservationId, Update(basedOn: T1), CancellationToken.None));
        }

        // One read, no PATCH.
        var read = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, read.Method);
    }

    [Fact]
    public async Task UpdateReservationAsync_APatchOfTheSameVersion_IsWrittenAndReadBack()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, Reservation(updateTime: "2026-11-06T08:00:05Z", checkIn: "2026-12-10", checkOut: "2026-12-13", guests: 2));
        h.Answer(HttpStatusCode.OK, "{}");
        h.Answer(HttpStatusCode.OK, Reservation(updateTime: "2026-11-06T08:30:00Z", checkIn: "2026-12-12", checkOut: "2026-12-15", guests: 3));

        OctorateReservation after;
        using (h.Scope.Use(OctorateSyncHarness.OrgA))
            after = await h.Client.UpdateReservationAsync(Target(), ReservationId, Update(basedOn: T1), CancellationToken.None);

        Assert.Equal([HttpMethod.Get, HttpMethod.Patch, HttpMethod.Get], h.Requests.Select(r => r.Method).ToList());
        Assert.EndsWith($"/rest/v1/reservation/585492/{ReservationId}", h.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"pmsProduct\":956527", h.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc), after.UpdatedAt);
    }

    [Fact]
    public async Task UpdateReservationAsync_A409OfOctorate_AndAnAcceptedPatchThatChangedNothing_AreNeverASuccess()
    {
        // 409 of the PATCH itself: a conflict.
        var refused = new ClientHarness();
        refused.Answer(HttpStatusCode.Conflict, """{"type":"Conflict"}""");
        using (refused.Scope.Use(OctorateSyncHarness.OrgA))
        {
            await Assert.ThrowsAsync<OctorateConflictException>(
                () => refused.Client.UpdateReservationAsync(Target(), ReservationId, Update(basedOn: null), CancellationToken.None));
        }

        // A 2xx and a reservation that does not hold the stay that was sent: refused, not a success.
        var ignored = new ClientHarness();
        ignored.Answer(HttpStatusCode.OK, "{}");
        ignored.Answer(HttpStatusCode.OK, Reservation(updateTime: "2026-11-06T08:00:05Z", checkIn: "2026-12-10", checkOut: "2026-12-13", guests: 2));
        using (ignored.Scope.Use(OctorateSyncHarness.OrgA))
        {
            await Assert.ThrowsAsync<OctorateRequestRejectedException>(
                () => ignored.Client.UpdateReservationAsync(Target(), ReservationId, Update(basedOn: null), CancellationToken.None));
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------------------

    private static OctorateBookingTarget Target() => new(OctorateSyncHarness.Accommodation, OctorateSyncHarness.Room, OctorateSyncHarness.PmsRoom);

    private static OctorateReservationUpdate Update(DateTime? basedOn) => new()
    {
        CheckIn = new DateOnly(2026, 12, 12),
        CheckOut = new DateOnly(2026, 12, 15),
        Guests = 3,
        Children = 0,
        RoomGross = 450m,
        BasedOnUpdateTime = basedOn,
    };

    private static string Reservation(string updateTime, string checkIn, string checkOut, int guests) =>
        $$"""{ "id": {{ReservationId}}, "refer": "K7Q2M9X4TB", "status": "CONFIRMED", "checkin": "{{checkIn}}", "checkout": "{{checkOut}}", "totalGuest": {{guests}}, "updateTime": "{{updateTime}}" }""";

    private static DomainEvent Event(
        DomainEventType type,
        Guid bookingId,
        BookingEventOrigin origin,
        BookingSource source = BookingSource.Direct,
        bool overwrite = false)
    {
        var payload = new BookingEventPayload(
            bookingId,
            Guid.NewGuid(),
            source,
            origin,
            BookingStatus.Confirmed,
            new DateOnly(2026, 12, 10),
            new DateOnly(2026, 12, 13))
        {
            Overwrite = overwrite,
        };
        return new DomainEvent(
            Guid.NewGuid(), OctorateSyncHarness.OrgA, type, bookingId, OctorateSyncHarness.Now.UtcDateTime, JsonSerializer.Serialize(payload, DomainEventRecorder.PayloadJsonOptions));
    }

    private static async Task<OctorateSyncState?> PushAsync(OctorateSyncHarness h, Guid bookingId, bool askedAgain, bool overwrite)
    {
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        await using var db = h.NewDb(tenant);
        return await h.Service(db, tenant).PushUpdateAsync(bookingId, askedAgain, overwrite, CancellationToken.None);
    }

    /// <summary>A confirmed booking of the host with its reservation on Octorate, last seen at <see cref="T1"/>, and the row of its create, synced.</summary>
    private static async Task<Guid> SeedMirroredAsync(
        OctorateSyncHarness h,
        bool withRow = true,
        BookingSource source = BookingSource.Manual,
        string bookingCode = "K7Q2M9X4TB",
        Action<Booking>? mutate = null,
        string reservationId = ReservationId)
    {
        var bookingId = await h.SeedBookingAsync(source: source, bookingCode: bookingCode);
        await using (var db = h.NewDb())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            booking.OctorateReservationId = reservationId;
            booking.OctorateUpdatedAt = T1;
            booking.UpdatedAt = T1.AddMinutes(-5);
            mutate?.Invoke(booking);
            await db.SaveChangesAsync();
        }

        if (withRow)
            await AddSyncAsync(h, bookingId, OctorateSyncState.Synced, OctorateSyncOperation.Create);

        return bookingId;
    }

    private static async Task AddSyncAsync(OctorateSyncHarness h, Guid bookingId, OctorateSyncState state, OctorateSyncOperation operation)
    {
        await using var db = h.NewDb();
        db.OctorateBookingSyncs.Add(new OctorateBookingSync
        {
            OrgId = OctorateSyncHarness.OrgA,
            BookingId = bookingId,
            PropertyId = h.PropertyId,
            State = state,
            LastOperation = operation,
            OctorateUpdateTime = T1,
            CreatedAt = T1.AddMinutes(-10),
            UpdatedAt = T1.AddMinutes(-10),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Octorate as the tests need it: one reservation with a version (its <c>updateTime</c>), the dates and the guests it holds, and the rule that a change
    /// based on another version than the one it holds is refused with a 409. Every change it receives is kept.
    /// </summary>
    private sealed class FakeOctorate
    {
        public FakeOctorate(OctorateSyncHarness h, DateTime updatedAt)
        {
            UpdatedAt = updatedAt;
            h.Client
                .Setup(c => c.UpdateReservationAsync(It.IsAny<OctorateBookingTarget>(), ReservationId, It.IsAny<OctorateReservationUpdate>(), It.IsAny<CancellationToken>()))
                .Returns((OctorateBookingTarget _, string _, OctorateReservationUpdate update, CancellationToken _) =>
                {
                    Updates.Add(update);
                    if (update.BasedOnUpdateTime is { } basedOn && basedOn != UpdatedAt)
                        return Task.FromException<OctorateReservation>(new OctorateConflictException());

                    CheckIn = update.CheckIn;
                    CheckOut = update.CheckOut;
                    Guests = update.Guests;
                    UpdatedAt = UpdatedAt.AddMinutes(1);
                    return Task.FromResult(AsReservation());
                });
            h.Client
                .Setup(c => c.GetReservationAsync(It.IsAny<OctorateBookingTarget>(), ReservationId, It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(AsReservation()));
        }

        public List<OctorateReservationUpdate> Updates { get; } = [];

        public DateTime UpdatedAt { get; private set; }

        public DateOnly CheckIn { get; private set; } = new(2026, 12, 10);

        public DateOnly CheckOut { get; private set; } = new(2026, 12, 13);

        public int Guests { get; private set; } = 2;

        /// <summary>Somebody changed the reservation in the channel manager.</summary>
        public void ChangedOutside(DateOnly checkIn, DateOnly checkOut, int guests, DateTime at)
        {
            CheckIn = checkIn;
            CheckOut = checkOut;
            Guests = guests;
            UpdatedAt = at;
        }

        public OctorateReservation AsReservation() => new()
        {
            Id = ReservationId,
            CheckIn = CheckIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CheckOut = CheckOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TotalGuests = Guests,
            UpdatedAt = UpdatedAt,
        };
    }

    /// <summary>The client over a handler that answers what the test queued and keeps every request with its body.</summary>
    private sealed class ClientHarness
    {
        public ClientHarness()
        {
            var clock = new FixedTimeProvider(OctorateSyncHarness.Now);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect",
                    ["Octorate:DirectChannelId"] = "270",
                })
                .Build();
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                new FakeTokens(),
                new OctorateRateLimiter(clock),
                Scope,
                configuration,
                clock,
                NullLogger<OctorateApiClient>.Instance);
        }

        public RecordingHandler Handler { get; } = new();

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }

        public IReadOnlyList<RecordedRequest> Requests => Handler.Requests;

        public void Answer(HttpStatusCode status, string body) => Handler.Answers.Enqueue((status, body));
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            var (status, answer) = Answers.Count > 0 ? Answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeTokens : IOctorateTokenService
    {
        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct) => Task.FromResult("token-" + orgId.ToString("N"));

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetPartnerAccessTokenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");

        public Task<string> RefreshRejectedPartnerTokenAsync(string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");
    }
}
