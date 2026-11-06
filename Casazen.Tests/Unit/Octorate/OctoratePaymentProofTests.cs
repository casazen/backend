using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The payments of a mirrored booking recorded on its Octorate reservation (E08/F01/US01/T04, AC-NF74-4, D9). The rule that costs money if it
/// fails: a payment is recorded once, for its exact amount, however many times its event is delivered and even when the answer of Octorate is
/// lost.
/// </summary>
public class OctoratePaymentProofTests
{
    private const string ReservationId = "130219355";

    /// <summary>
    /// PROOF TEST: money rule. A cash payment of 123.45 of a mirrored booking is delivered twice: Octorate receives one registration, with the
    /// transaction <c>casazen-{paymentId}</c>, the configured mode and 123.45. A Stripe payment whose registration reached Octorate but whose
    /// answer was lost is delivered again: it is found on the reservation and not sent a second time.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_RegisteredOnce_AmountExact_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync();
        await MirrorAsync(h, bookingId);
        var cash = await SeedPaymentAsync(h, bookingId, 123.45m, PaymentMethod.CashOnArrival, stripePaymentIntentId: null);
        var card = await SeedPaymentAsync(h, bookingId, 50.10m, PaymentMethod.CreditCard, stripePaymentIntentId: "pi_3LostAnswer");

        var registered = new List<OctoratePaymentRegistration>();
        h.Client
            .Setup(c => c.GetReservationAsync(OctorateSyncHarness.Accommodation, ReservationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OctorateReservation { Id = ReservationId, PaymentTransactions = registered.Select(r => r.Transaction).ToList() });
        h.Client
            .Setup(c => c.RegisterPaymentAsync(OctorateSyncHarness.Accommodation, ReservationId, It.IsAny<OctoratePaymentRegistration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, OctoratePaymentRegistration payment, CancellationToken _) =>
            {
                registered.Add(payment);

                // The card payment reaches Octorate, then its answer is lost.
                return payment.Transaction == "pi_3LostAnswer" && registered.Count(r => r.Transaction == "pi_3LostAnswer") == 1
                    ? Task.FromException<OctoratePaymentResult>(new OctorateServerException(null))
                    : Task.FromResult(new OctoratePaymentResult("21575084"));
            });

        // The cash payment, delivered twice.
        await DeliverAsync(h, cash);
        await DeliverAsync(h, cash);

        // The card payment: the first delivery loses the answer (the dispatcher delivers it again), the second finds it on the reservation.
        await Assert.ThrowsAsync<OctorateServerException>(() => DeliverAsync(h, card));
        await DeliverAsync(h, card);

        var cashRegistration = Assert.Single(registered, r => r.Transaction == $"casazen-{cash}");
        Assert.Equal("CASH", cashRegistration.PaymentMode);
        Assert.Equal(123.45m, cashRegistration.Amount);
        var cardRegistration = Assert.Single(registered, r => r.Transaction == "pi_3LostAnswer");
        Assert.Equal("CREDITCARD", cardRegistration.PaymentMode);
        Assert.Equal(50.10m, cardRegistration.Amount);
        Assert.Equal(2, registered.Count);

        await using var read = h.NewDb();
        var payments = await read.Payments.AsNoTracking().Where(p => p.BookingId == bookingId).ToDictionaryAsync(p => p.Id);
        Assert.Equal($"casazen-{cash}", payments[cash].OctorateTransactionId);
        Assert.Equal("pi_3LostAnswer", payments[card].OctorateTransactionId);
        Assert.NotNull(payments[cash].OctorateRegisteredAt);
        Assert.NotNull(payments[card].OctorateRegisteredAt);
    }

    /// <summary>
    /// PROOF TEST: money rule. The payment event arrives before the confirmation of the booking (the booking site records the payment first and
    /// the dispatcher delivers in that order): it is not consumed with no effect, it is delivered again; the confirmation then creates the
    /// reservation and records the payment paid before it; the payment event delivered once more sends nothing twice. The payment ends on
    /// Octorate, once, for its exact amount.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_BeforeTheConfirmation_IsDeliveredAgainAndEndsOnOctorate_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync(source: BookingSource.Direct);
        var card = await SeedPaymentAsync(h, bookingId, 100m, PaymentMethod.CreditCard, stripePaymentIntentId: "pi_3Site");
        await WaitForConfirmationAsync(h, bookingId);
        var registered = ServeOctorate(h);

        // 1. The payment event comes first: the create is still to come, so the event is not consumed, it is delivered again.
        await Assert.ThrowsAsync<OctorateSyncPendingException>(() => DeliverAsync(h, card));
        Assert.Empty(registered);

        // 2. The confirmation: the reservation is created and the payment paid before it is recorded.
        Assert.Equal(OctorateSyncState.Synced, await ConfirmAsync(h, bookingId));
        var registration = Assert.Single(registered);
        Assert.Equal("pi_3Site", registration.Transaction);
        Assert.Equal("CREDITCARD", registration.PaymentMode);
        Assert.Equal(100m, registration.Amount);

        // 3. The payment event delivered again finds it recorded and sends nothing.
        await DeliverAsync(h, card);
        Assert.Single(registered);

        await using var read = h.NewDb();
        Assert.Equal("pi_3Site", (await read.Payments.AsNoTracking().SingleAsync(p => p.Id == card)).OctorateTransactionId);
        Assert.Equal(OctorateSyncState.Synced, (await read.OctorateBookingSyncs.AsNoTracking().SingleAsync(s => s.BookingId == bookingId)).State);
    }

    /// <summary>
    /// A payment of a booking that no create is waiting for (it was confirmed before the property was mirrored, so nothing will ever create its
    /// reservation) is not held back: the event is consumed, not delivered again until it is dead-lettered, and nothing is sent.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_OfABookingNoCreateWaitsFor_IsConsumedAndSendsNothing_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync(source: BookingSource.Direct);
        var card = await SeedPaymentAsync(h, bookingId, 100m, PaymentMethod.CreditCard, stripePaymentIntentId: "pi_3Old");
        var registered = ServeOctorate(h);

        await DeliverAsync(h, card);

        Assert.Empty(registered);
        await using var read = h.NewDb();
        Assert.Null((await read.Payments.AsNoTracking().SingleAsync(p => p.Id == card)).OctorateTransactionId);
        Assert.False(await read.OctorateBookingSyncs.AnyAsync());
    }

    /// <summary>
    /// PROOF TEST: money rule. The reservation is created but the recording of a payment paid before it gets a 5xx: the error is not swallowed
    /// (the confirmation fails and is delivered again), the mirror is marked <c>Failed</c> with <c>octorate_payments_not_recorded</c> (the host
    /// sees it), the payment stays to be recorded, and the delivery that follows (the reservation id is saved already) records it, once.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_ServerErrorWhileTheCreateRecordsIt_MarksTheMirror_AndIsNotLost_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync(source: BookingSource.Direct);
        var cash = await SeedPaymentAsync(h, bookingId, 80m, PaymentMethod.CashOnArrival, stripePaymentIntentId: null);
        var registered = ServeOctorate(h, failFirstRead: true);

        // The create succeeds; reading the reservation to record the payment answers 503. The error goes up, nothing was sent.
        await Assert.ThrowsAsync<OctorateServerException>(() => ConfirmAsync(h, bookingId));
        Assert.Empty(registered);
        Assert.Equal(ReservationId, (await h.ReadBookingAsync(bookingId)).OctorateReservationId);
        var marked = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(marked);
        Assert.Equal(OctorateSyncState.Failed, marked.State);
        Assert.Equal(OctorateSyncErrorCodes.PaymentsNotRecorded, marked.ErrorCode);
        await using (var read = h.NewDb())
        {
            Assert.Null((await read.Payments.AsNoTracking().SingleAsync(p => p.Id == cash)).OctorateTransactionId);
        }

        // The next delivery of the confirmation finds the reservation and records what is left: once, and the mirror is closed.
        Assert.Equal(OctorateSyncState.Synced, await ConfirmAsync(h, bookingId));
        var registration = Assert.Single(registered);
        Assert.Equal($"casazen-{cash}", registration.Transaction);
        Assert.Equal(80m, registration.Amount);
        var closed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(closed);
        Assert.Equal(OctorateSyncState.Synced, closed.State);
        Assert.Null(closed.ErrorCode);
        h.Client.Verify(
            c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The reservation exists but its mirror is marked <c>Failed</c> because a payment could not be recorded. The event of that payment comes
    /// next (the dispatcher delivers it before the confirmation is delivered again): it is not consumed with no effect, it records the payment
    /// with the others and closes the mirror, and the confirmation delivered after it finds nothing left to send.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_EventWhileTheMirrorIsMarked_RecordsThePayment_AndClosesTheMirror_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync(source: BookingSource.Direct);
        var cash = await SeedPaymentAsync(h, bookingId, 80m, PaymentMethod.CashOnArrival, stripePaymentIntentId: null);
        var registered = ServeOctorate(h, failFirstRead: true);
        await Assert.ThrowsAsync<OctorateServerException>(() => ConfirmAsync(h, bookingId));
        Assert.Empty(registered);

        await DeliverAsync(h, cash);

        Assert.Equal($"casazen-{cash}", Assert.Single(registered).Transaction);
        var closed = await h.ReadSyncAsync(bookingId);
        Assert.NotNull(closed);
        Assert.Equal(OctorateSyncState.Synced, closed.State);
        Assert.Null(closed.ErrorCode);

        Assert.Equal(OctorateSyncState.Synced, await ConfirmAsync(h, bookingId));
        Assert.Single(registered);
    }

    /// <summary>
    /// The payment of a reservation that exists does not depend on the read of the PMS rooms (which only the create needs): Octorate is not asked
    /// for them, and the payment is recorded even when that read would fail.
    /// </summary>
    [Fact]
    public async Task OctoratePayment_OfASyncedBooking_DoesNotReadThePmsRooms_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await h.SeedBookingAsync();
        await MirrorAsync(h, bookingId);
        var cash = await SeedPaymentAsync(h, bookingId, 100m, PaymentMethod.CashOnArrival, stripePaymentIntentId: null);
        var registered = ServeOctorate(h);
        h.Client
            .Setup(c => c.GetPmsRoomsAsync(OctorateSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        await DeliverAsync(h, cash);

        Assert.Equal($"casazen-{cash}", Assert.Single(registered).Transaction);
        h.Client.Verify(c => c.GetPmsRoomsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Octorate creates the reservation, keeps the payments it is sent and lists their transactions on the single read.</summary>
    private static List<OctoratePaymentRegistration> ServeOctorate(OctorateSyncHarness h, bool failFirstRead = false)
    {
        var registered = new List<OctoratePaymentRegistration>();
        var reads = 0;
        h.Client
            .Setup(c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation
            {
                Id = ReservationId,
                Refer = "K7Q2M9X4TB",
                UpdatedAt = new DateTime(2026, 11, 6, 8, 0, 5, DateTimeKind.Utc),
            });
        h.Client
            .Setup(c => c.GetReservationAsync(OctorateSyncHarness.Accommodation, ReservationId, It.IsAny<CancellationToken>()))
            .Returns(() => failFirstRead && ++reads == 1
                ? Task.FromException<OctorateReservation>(new OctorateServerException(503))
                : Task.FromResult(new OctorateReservation { Id = ReservationId, PaymentTransactions = registered.Select(r => r.Transaction).ToList() }));
        h.Client
            .Setup(c => c.RegisterPaymentAsync(OctorateSyncHarness.Accommodation, ReservationId, It.IsAny<OctoratePaymentRegistration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, OctoratePaymentRegistration payment, CancellationToken _) =>
            {
                registered.Add(payment);
                return Task.FromResult(new OctoratePaymentResult("21575084"));
            });
        return registered;
    }

    /// <summary>The confirmation of the booking waits in the outbox, as the booking site records it together with the payment.</summary>
    private static async Task WaitForConfirmationAsync(OctorateSyncHarness h, Guid bookingId)
    {
        await using var db = h.NewDb();
        db.OutboxEvents.Add(new OutboxEvent
        {
            OrgId = OctorateSyncHarness.OrgA,
            EventType = DomainEventType.BookingConfirmed,
            EntityId = bookingId,
            OccurredAt = OctorateSyncHarness.Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>One delivery of <c>BookingConfirmed</c> to the push, in its own scope of the event bus, under org A.</summary>
    private static async Task<OctorateSyncState?> ConfirmAsync(OctorateSyncHarness h, Guid bookingId)
    {
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        await using var db = h.NewDb(tenant);
        return await h.Service(db, tenant).PushCreateAsync(bookingId, CancellationToken.None);
    }

    /// <summary>One delivery of <c>PaymentSucceeded</c> in its own scope of the event bus, under org A, with the connector on.</summary>
    private static async Task DeliverAsync(OctorateSyncHarness h, Guid paymentId)
    {
        var tenant = new RequestTenant(OctorateSyncHarness.OrgA);
        await using var db = h.NewDb(tenant);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var handler = new OctoratePaymentHandler(h.Service(db, tenant), flags.Object);
        await handler.HandleAsync(
            new DomainEvent(Guid.NewGuid(), OctorateSyncHarness.OrgA, DomainEventType.PaymentSucceeded, paymentId, OctorateSyncHarness.Now.UtcDateTime, "{}"),
            CancellationToken.None);
    }

    /// <summary>The booking is on Octorate: its reservation id and a synced mirror.</summary>
    private static async Task MirrorAsync(OctorateSyncHarness h, Guid bookingId)
    {
        await using var db = h.NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.OctorateReservationId = ReservationId;
        db.OctorateBookingSyncs.Add(new OctorateBookingSync
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            PropertyId = booking.PropertyId,
            State = OctorateSyncState.Synced,
            LastOperation = OctorateSyncOperation.Create,
            Attempts = 1,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedPaymentAsync(
        OctorateSyncHarness h,
        Guid bookingId,
        decimal amount,
        PaymentMethod method,
        string? stripePaymentIntentId)
    {
        await using var db = h.NewDb();
        var payment = new Payment
        {
            OrgId = OctorateSyncHarness.OrgA,
            BookingId = bookingId,
            Amount = amount,
            Status = PaymentStatus.Completed,
            Method = method,
            TransactionId = stripePaymentIntentId ?? $"manual:{Guid.NewGuid():N}",
            StripePaymentIntentId = stripePaymentIntentId,
            ProcessedAt = new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }
}
