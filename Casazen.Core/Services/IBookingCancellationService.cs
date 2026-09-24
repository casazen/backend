using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Cancellation of a booking by the host with its money on Stripe (BK-02, #51, A3-05): intents not paid yet are
/// canceled, what was paid is refunded for the amount the host chooses, never less than a rule of the model grants the
/// guest (<see cref="CancellationRefundPolicy"/>) and never more than what was paid. Callers authorize the booking
/// (and the payments when money is refunded) first: the service never checks roles.
/// </summary>
public interface IBookingCancellationService
{
    /// <summary>What a cancellation of the booking would do now, for the confirmation dialog.</summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    Task<BookingCancellationQuote> GetQuoteAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the booking: cancels the unpaid PaymentIntent / SetupIntent on the connected account, sets the booking
    /// Cancelled and refunds <see cref="BookingCancellationRequest.RefundAmount"/> of what was paid through Stripe.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Not cancellable, refund amount missing or out of range.</exception>
    /// <exception cref="Exceptions.DomainConflictException">Already cancelled, or a payment is completing right now.</exception>
    Task<BookingCancellationResult> CancelAsync(BookingCancellationRequest request, CancellationToken cancellationToken = default);
}

/// <param name="RefundAmount">
/// Amount to refund of what was paid through Stripe; required (0 allowed) when something was paid, and at least
/// <see cref="BookingCancellationQuote.MinimumRefundAmount"/>.
/// </param>
/// <param name="Reason">
/// The host's reason, kept on the booking (<see cref="Booking.CancellationNote"/>) and on its refunds; never sent to the
/// guest.
/// </param>
public sealed record BookingCancellationRequest(
    Guid BookingId,
    decimal? RefundAmount,
    string? Reason,
    string? RequestedByUserId);

/// <param name="Cancellable">False for bookings already cancelled or checked out.</param>
/// <param name="PaidAmount">Paid through Stripe (sum of the paid Stripe payments).</param>
/// <param name="RefundedAmount">Already refunded and confirmed by Stripe.</param>
/// <param name="PendingRefundAmount">Refunds in progress.</param>
/// <param name="RefundableAmount">Maximum refund now.</param>
/// <param name="MinimumRefundAmount">Minimum refund now under <paramref name="Rule"/> (0 without a rule).</param>
/// <param name="Rule">The rule of the model that sets the minimum.</param>
/// <param name="FreeCancellationUntil">Last day of free cancellation shown to the guest, when the booking has one.</param>
/// <param name="CancellationPolicyName">Name of the property's cancellation policy, when it has one.</param>
/// <param name="OfflinePaidAmount">Paid outside Stripe (cash, bank transfer): CasaZen cannot refund it.</param>
/// <param name="HasUncollectedIntent">An unpaid PaymentIntent or a saved card (SetupIntent) will be canceled.</param>
public sealed record BookingCancellationQuote(
    Guid BookingId,
    BookingStatus Status,
    bool Cancellable,
    string Currency,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal PendingRefundAmount,
    decimal RefundableAmount,
    decimal MinimumRefundAmount,
    CancellationRefundRule Rule,
    DateTime? FreeCancellationUntil,
    string? CancellationPolicyName,
    decimal OfflinePaidAmount,
    bool HasUncollectedIntent)
{
    /// <summary>Something paid through Stripe can still be refunded: the host must choose the amount.</summary>
    public bool RequiresRefundDecision => RefundableAmount > 0;
}

/// <param name="Refunds">Refunds created by the cancellation, as Stripe left them (succeeded, pending, failed).</param>
/// <param name="CanceledIntents">Unpaid PaymentIntents / SetupIntents canceled on Stripe.</param>
public sealed record BookingCancellationResult(
    Booking Booking,
    IReadOnlyList<PaymentRefund> Refunds,
    int CanceledIntents);
