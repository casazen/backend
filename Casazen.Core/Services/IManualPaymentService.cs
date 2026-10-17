using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// A payment the host received outside CasaZen (cash or bank transfer) and records on a booking (SR-06b). The money did not
/// move through Stripe, so the record is a fact: it is born <see cref="PaymentStatus.Completed"/>, settled on the day the host
/// says, and never goes above what the booking still has to collect. Nothing is sent to anyone.
/// </summary>
public interface IManualPaymentService
{
    /// <summary>
    /// Records the payment under the booking's lock, once per <see cref="ManualPaymentCommand.IdempotencyKey"/>: the same key again
    /// gives back the payment it created (<see cref="ManualPaymentResult.Created"/> false) and writes nothing. 422
    /// <c>payment_method_not_allowed</c> (only cash and bank transfer), <c>payment_amount_invalid</c>, <c>payment_received_on_future</c>,
    /// <c>payment_booking_cancelled</c>, <c>payment_booking_from_portal</c> (the portal collects those), <c>payment_exceeds_balance</c>
    /// (more than the booking still has to collect); 409 <c>payment_idempotency_key_reused</c> (the key belongs to a different record).
    /// </summary>
    Task<ManualPaymentResult> RecordAsync(ManualPaymentCommand command, CancellationToken cancellationToken = default);
}

/// <summary>What the host records. The booking is already known to be one the caller may write payments of.</summary>
/// <param name="BookingId">The booking the money is for.</param>
/// <param name="Amount">In euros, more than zero, two decimals at most.</param>
/// <param name="Method"><see cref="PaymentMethod.CashOnArrival"/> or <see cref="PaymentMethod.BankTransfer"/>.</param>
/// <param name="ReceivedOn">The day the money came in (Europe/Rome), today or before; today when missing.</param>
/// <param name="Description">A note of the host; optional.</param>
/// <param name="IdempotencyKey">The key the client chose for this record (header <c>Idempotency-Key</c>), at most 100 characters.</param>
/// <param name="ApplyOtaWithholding">Passed to the fiscal rule as before; portal stays are refused here, so it never applies.</param>
/// <param name="ManualWithholdingTax">A withholding the host states by hand, as before.</param>
public sealed record ManualPaymentCommand(
    Guid BookingId,
    decimal Amount,
    PaymentMethod Method,
    DateOnly? ReceivedOn,
    string? Description,
    string IdempotencyKey,
    bool? ApplyOtaWithholding = null,
    decimal? ManualWithholdingTax = null);

/// <summary>The payment, and whether this call wrote it (<c>false</c>: the same key had already been recorded).</summary>
public sealed record ManualPaymentResult(Payment Payment, bool Created);
