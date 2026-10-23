namespace Casazen.Core.Services;

/// <summary>
/// Closes the checkout holds that the expiry job left "to the payment webhook" and that the webhook never settled
/// (<see cref="ICheckoutHoldExpiryService"/>, BK-21): a <see cref="Entities.BookingStatus.Pending"/> booking whose payment has been
/// <see cref="Entities.PaymentStatus.Processing"/> for longer than <see cref="GraceMinutes"/>. Stripe is asked for the real state of
/// the PaymentIntent, on the connected account it lives on:
/// <list type="bullet">
/// <item><c>succeeded</c>: settled exactly as the <c>payment_intent.succeeded</c> webhook does (booking confirmed, confirmed again
/// when the hold had ended and the dates are free, or full refund when they are taken); the webhook arriving later finds it done;</item>
/// <item><c>processing</c> or <c>requires_capture</c>: still in flight, left alone;</item>
/// <item>anything else (never paid, canceled, unknown to Stripe): the payment goes back to <see cref="Entities.PaymentStatus.Pending"/>,
/// so the expiry job cancels the intent and the booking and releases the dates on its next run.</item>
/// </list>
/// A booking is never left pending for ever because a webhook was lost, rejected or never configured.
/// </summary>
public interface ICheckoutPaymentReconciliationService
{
    /// <summary>A payment in flight is read again from Stripe only after this long (the webhook normally arrives in seconds).</summary>
    const int GraceMinutes = 10;

    /// <summary>Most payments handled by one run; the rest waits for the next one.</summary>
    const int BatchSize = 50;

    Task<CheckoutPaymentReconciliationRun> ReconcileInFlightPaymentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one reconciliation run.</summary>
/// <param name="Settled">Payments Stripe reports as succeeded, applied to their booking.</param>
/// <param name="StillInFlight">Payments Stripe still reports as processing: left alone.</param>
/// <param name="Released">Payments that were never paid: back to pending for the expiry job.</param>
/// <param name="Failed">Payments not handled because of an error (Stripe unreachable...): retried by the next run.</param>
public sealed record CheckoutPaymentReconciliationRun(int Settled, int StillInFlight, int Released, int Failed)
{
    public static CheckoutPaymentReconciliationRun Empty { get; } = new(0, 0, 0, 0);
}
