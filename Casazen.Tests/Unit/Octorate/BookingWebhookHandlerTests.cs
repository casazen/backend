using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The handlers of the notifications of Octorate about reservations (E02/F02/US01/T02, ADR-031, ADR-022): the body of a notification is a signal
/// and is trusted for nothing, so the reservation is re-read before any effect; a repeated or out-of-order notification changes nothing and
/// creates no second booking (data rule); a cancellation keeps the amounts Octorate reports, the cancellation fee included (money rule); the
/// payload is never logged (privacy rule); an inbound change sends nothing back.
/// </summary>
public class BookingWebhookHandlerTests
{
    // ─── Which handler takes which notification ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OctorateWebhookEvents.ReservationCreated, true, false, false)]
    [InlineData(OctorateWebhookEvents.ReservationConfirmed, true, false, false)]
    [InlineData(OctorateWebhookEvents.ReservationChange, false, true, false)]
    [InlineData(OctorateWebhookEvents.ReservationCancelled, false, false, true)]
    [InlineData(OctorateWebhookEvents.ChatMessageReceived, false, false, false)]
    [InlineData(OctorateWebhookEvents.ContentPush, false, false, false)]
    public async Task EachHandlerTakesItsOwnKindOfNotification(string webhookType, bool created, bool modified, bool cancelled)
    {
        // One run for each handler with a client of its own, so that the reads of each can be counted.
        foreach (var (name, reads) in new[] { ("created", created), ("modified", modified), ("cancelled", cancelled) })
        {
            var h = new BookingImportHarness();
            h.Serve(Res("7001"));
            await using var db = h.NewDb();
            OctorateBookingWebhookHandler handler = name switch
            {
                "created" => Created(h, db),
                "modified" => Modified(h, db),
                _ => Cancelled(h, db),
            };

            await handler.HandleAsync(Notification(h, webhookType), CancellationToken.None);

            // The handler of that kind of notification reads the list once (its first page; the list is read until an empty page); every
            // other, and any notification that is not about a reservation (a chat message, a content push), reads nothing.
            h.Client.Verify(
                c => c.GetReservationsAsync(Accommodation, It.Is<OctorateReservationQuery>(q => q.Page == 0), It.IsAny<CancellationToken>()),
                reads ? Times.Once() : Times.Never());
        }
    }

    [Fact]
    public async Task TheNotificationIsASignal_TheReadsStartTwoDaysBeforeItArrivedAndAskForModifiedReservations()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        await Created(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated, objectId: null), CancellationToken.None);

        // Whatever the body said (here: no reservation id at all), the reservations changed since 4 November (Rome) are read, and applied.
        h.Client.Verify(
            c => c.GetReservationsAsync(
                Accommodation,
                It.Is<OctorateReservationQuery>(q => q.Type == OctorateReservationListType.ModifiedBetween
                                                     && q.From == new DateOnly(2026, 11, 4)
                                                     && q.Page == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal("7001", (await h.ReadBookingAsync("7001")).OctorateReservationId);
    }

    // ─── Once, in order ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingWebhook_ARepeatedOrLateNotification_ChangesNothingAndCreatesNoSecondBooking_Test()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var created = Created(h, db);
        var modified = Modified(h, db);

        // The same notification three times, and a "change" notification for a reservation that did not change.
        for (var i = 0; i < 3; i++)
            await created.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);
        await modified.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationChange), CancellationToken.None);

        await using var read = h.NewDb();
        Assert.Equal(1, await read.Bookings.CountAsync(b => b.OctorateReservationId == "7001"));
        Assert.Equal(2, (await h.ReadEventsAsync()).Count);
        h.Client.Verify(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>()), Times.Once);

        // The newer version, then the notification of the older one arriving late: the booking keeps the newer.
        h.Serve(Res("7001") with { UpdatedAt = SecondVersion, CheckIn = "2026-11-24", CheckOut = "2026-11-27" });
        await modified.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationChange), CancellationToken.None);
        h.Serve(Res("7001"));
        await modified.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationChange), CancellationToken.None);

        var booking = await h.ReadBookingAsync("7001");
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), booking.CheckInDate);
        Assert.Equal(SecondVersion, booking.OctorateUpdatedAt);
        Assert.Equal(1, (await h.ReadEventsAsync()).Count(e => e.EventType == DomainEventType.BookingModified));
    }

    [Fact]
    public async Task AnInboundChange_NeverSendsAnythingBackToOctorate()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        await Created(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);
        h.Serve(Res("7001", OctorateBookingStatus.Cancelled) with { UpdatedAt = SecondVersion });
        await Cancelled(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCancelled), CancellationToken.None);

        // Only reads: the list and the single read. The cancellation arrived from Octorate and is not echoed to it.
        h.Client.Verify(c => c.CancelReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.SetCheckInStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateCheckInStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.UpdatePropertyAsync(It.IsAny<string>(), It.IsAny<OctoratePropertyUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Cancelled, (await h.ReadBookingAsync("7001")).Status);
    }

    // ─── Money ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingWebhook_CancellationFee_Preserved_Test()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001") with { TotalPaid = 100m });
        await using var db = h.NewDb();
        await Created(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);

        // The guest cancels after the free period: Octorate reports the reservation cancelled with what the channel kept (a fee of 35.50 paid, a
        // commission on it of 5.25) in the payments and the breakdown of the single read.
        h.Serve(Res("7001", OctorateBookingStatus.Cancelled) with
        {
            UpdatedAt = SecondVersion,
            CancelledAt = SecondVersion,
            TotalGross = 330m,
            RoomGross = 300m,
            TotalPaid = 35.50m,
            PriceBreakdown = [new OctoratePriceComponent("ROOM_NET", 300m), new OctoratePriceComponent("COMMISSION", 5.25m)],
        });
        await Cancelled(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCancelled), CancellationToken.None);

        var booking = await h.ReadBookingAsync("7001");
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(SecondVersion, booking.CancelledAt);
        // What Octorate reports, to the cent: the cancellation does not reset it, and CasaZen invents no refund or payment.
        Assert.Equal(35.50m, booking.OtaPaidAmount);
        Assert.Equal(5.25m, booking.OtaCommission);
        Assert.Equal(330m, booking.TotalPrice);
        Assert.Null(booking.OtaNetAmount);
        Assert.Null(booking.OtaBalanceAmount);
        await using var read = h.NewDb();
        Assert.Empty(await read.Payments.ToListAsync());
        Assert.Equal(1, (await h.ReadEventsAsync()).Count(e => e.EventType == DomainEventType.BookingCancelled));
    }

    // ─── Privacy ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingWebhook_PayloadNotLogged_Test()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var log = new RecordingLogger<BookingCreatedHandler>();
        var handler = new BookingCreatedHandler(db, h.Service(db), h.Scope(), Flags(true), log);
        var notification = Notification(h, OctorateWebhookEvents.ReservationCreated, objectId: "RES-ZZ-4711");

        await handler.HandleAsync(notification, CancellationToken.None);

        // One line: the id of the event, its type and that it was processed; nothing of the stored payload, no accommodation, no reservation id.
        var line = Assert.Single(log.Lines);
        Assert.Contains(notification.Id.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(OctorateWebhookEvents.ReservationCreated, line, StringComparison.Ordinal);
        Assert.Contains("processed", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(notification.PayloadJson, line, StringComparison.Ordinal);
        Assert.DoesNotContain(Accommodation, line, StringComparison.Ordinal);
        Assert.DoesNotContain("RES-ZZ-4711", line, StringComparison.Ordinal);
    }

    // ─── What stops the handler ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheConnectorOff_NothingIsRead()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var handler = new BookingCreatedHandler(db, h.Service(db), h.Scope(), Flags(false), Logger<BookingCreatedHandler>());

        await handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);

        h.Client.VerifyNoOtherCalls();
        Assert.Empty(await db.Bookings.ToListAsync());
    }

    [Theory]
    [InlineData(OctorateMappingStatus.Rejected)]
    [InlineData(OctorateMappingStatus.Proposed)]
    [InlineData(OctorateMappingStatus.Ambiguous)]
    public async Task AListingThatIsNoLongerConfirmed_IsLeftAlone(OctorateMappingStatus status)
    {
        var h = new BookingImportHarness(status: status);
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        await Created(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);

        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ANotificationOfAnotherOrgOrOfAnotherAccommodation_IsNotTrusted()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var handler = Created(h, db);

        // The mapping is org A's: an event that names it for org B finds nothing; an event whose accommodation is not the mapping's neither.
        await handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated, orgId: OrgB), CancellationToken.None);
        await handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated, accommodation: "999999"), CancellationToken.None);
        await handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated, mappingId: Guid.NewGuid()), CancellationToken.None);

        h.Client.VerifyNoOtherCalls();
        Assert.Empty(await db.Bookings.ToListAsync());
    }

    [Fact]
    public async Task AnOrgThatDisconnected_EndsQuietly_AnErrorOfOctorateIsDeliveredAgain()
    {
        var h = new BookingImportHarness();
        await using var db = h.NewDb();
        var handler = Created(h, db);

        h.Client
            .Setup(c => c.GetReservationsAsync(Accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.NotConnected());
        await handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None);

        h.Client
            .Setup(c => c.GetReservationsAsync(Accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await Assert.ThrowsAsync<OctorateServerException>(
            () => handler.HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None));
    }

    [Fact]
    public async Task ARunThatStopsBeforeTheEnd_IsDeliveredAgain()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateRateLimitException(429));
        await using var db = h.NewDb();

        await Assert.ThrowsAsync<BookingImportIncompleteException>(
            () => Created(h, db).HandleAsync(Notification(h, OctorateWebhookEvents.ReservationCreated), CancellationToken.None));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static BookingCreatedHandler Created(BookingImportHarness h, AppDbContext db) =>
        new(db, h.Service(db), h.Scope(), Flags(true), Logger<BookingCreatedHandler>());

    private static BookingModifiedHandler Modified(BookingImportHarness h, AppDbContext db) =>
        new(db, h.Service(db), h.Scope(), Flags(true), Logger<BookingModifiedHandler>());

    private static BookingCancelledHandler Cancelled(BookingImportHarness h, AppDbContext db) =>
        new(db, h.Service(db), h.Scope(), Flags(true), Logger<BookingCancelledHandler>());

    private static IFeatureFlags Flags(bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        return flags.Object;
    }

    private static ILogger<T> Logger<T>() => new RecordingLogger<T>();

    /// <summary>The event the webhook controller writes for a notification, as the handler receives it (ids only, as stored).</summary>
    private static OctorateWebhookReceivedEvent Notification(
        BookingImportHarness h,
        string webhookType,
        string? objectId = "7001",
        Guid? orgId = null,
        string accommodation = Accommodation,
        Guid? mappingId = null)
    {
        var payload = JsonSerializer.Serialize(new OctorateWebhookPayload(webhookType, accommodation, objectId, Now.UtcDateTime));
        return new OctorateWebhookReceivedEvent(
            Guid.NewGuid(),
            orgId ?? OrgA,
            DomainEventType.OctorateWebhookReceived,
            mappingId ?? h.MappingId,
            Now.UtcDateTime,
            payload);
    }

    /// <summary>Keeps what is logged, formatted, to prove what a line holds.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}
