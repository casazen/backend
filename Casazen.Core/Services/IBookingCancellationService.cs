using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Cancellation of a booking with its money on Stripe (BK-02, #51, A3-05): intents not paid yet are canceled, what was
/// paid is refunded for the amount the caller decides, never less than the least the <see cref="BookingCancellationRequest.Initiator"/>
/// owes the guest (the host: everything refundable; the guest: what the cancellation policy grants,
/// <see cref="CancellationRefundPolicy"/>) and never more than what was paid. Callers authorize the booking (and the
/// payments when money is refunded) first: the service never checks roles.
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
    /// <remarks>
    /// Under its lock the service reads the booking and its payments again, whatever the caller read before: the
    /// context's change tracker is cleared, so entities the caller still holds from that context are no longer tracked.
    /// A caller therefore saves its own changes first and does not call this inside a transaction of its own. When the
    /// guest cancels, the guest's cancel link is spent in the same transaction (the hash stays, the expiry becomes the
    /// moment of the cancellation).
    /// </remarks>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Not cancellable, refund amount missing or out of range.</exception>
    /// <exception cref="Exceptions.DomainConflictException">Already cancelled, or a payment is completing right now.</exception>
    Task<BookingCancellationResult> CancelAsync(BookingCancellationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Who cancels the booking: it sets the least refund the cancellation accepts (BK-02, PO 2026-10-08).</summary>
public enum BookingCancellationInitiator
{
    /// <summary>
    /// The host: the guest always gets back everything that can be refunded, whatever the cancellation policy says.
    /// </summary>
    Host,

    /// <summary>
    /// The guest, cancelling themselves: the guest gets back what the property's cancellation policy grants
    /// (<see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>), never more than
    /// what can be refunded.
    /// </summary>
    Guest,
}

/// <param name="RefundAmount">
/// Amount to refund of what was paid through Stripe; required (0 allowed) when something was paid, and at least
/// <see cref="BookingCancellationQuote.MinimumRefundAmount"/> of the <paramref name="Initiator"/>. A guest who gets
/// nothing from the policy may leave it null. For a guest it is the decision the caller already took from
/// <see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>: the cancellation
/// recalculates the policy's refund under its lock and refuses an amount below it (and, as always, above what is
/// refundable). Between the two the amount is the caller's: it is the figure the guest was shown or asked for.
/// </param>
/// <param name="Reason">
/// The host's reason, kept on the booking (<see cref="Booking.CancellationNote"/>) and on its refunds; never sent to the
/// guest.
/// </param>
/// <param name="RequestedByUserId">Auth0 subject of the host who cancels; null for a guest or the system.</param>
/// <param name="Initiator">Who cancels; the host unless the caller says otherwise.</param>
public sealed record BookingCancellationRequest(
    Guid BookingId,
    decimal? RefundAmount,
    string? Reason,
    string? RequestedByUserId,
    BookingCancellationInitiator Initiator = BookingCancellationInitiator.Host);

/// <param name="Cancellable">False for bookings already cancelled or checked out.</param>
/// <param name="PaidAmount">Paid through Stripe (sum of the paid Stripe payments).</param>
/// <param name="RefundedAmount">Already refunded and confirmed by Stripe.</param>
/// <param name="PendingRefundAmount">Refunds in progress.</param>
/// <param name="RefundableAmount">Maximum refund now.</param>
/// <param name="MinimumRefundAmount">
/// Minimum refund now when the host cancels: everything refundable (PO 2026-10-08). The service computes the guest's
/// own minimum, the policy's refund, when the guest cancels.
/// </param>
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
