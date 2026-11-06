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
