using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest self-service cancellation via a signed one-time link (BK-02, BK-07, PO 2026-10-08).
/// <list type="bullet">
/// <item>The host (or the system) sends a cancel link to the guest's email address.</item>
/// <item>The guest clicks the link, which calls the public endpoint with the token.</item>
/// <item>The refund is computed exclusively from the property's <see cref="CancellationPolicy"/>;
///   the CasaZen floor (<see cref="Booking.FreeRefundDeadline"/>) does not apply to guest-initiated
///   cancellations. The refund base is <see cref="Booking.BasePrice"/> (tourist tax excluded).</item>
/// </list>
/// Callers do not check authorisation before calling: host endpoints are guarded by the booking policy;
/// the public endpoint validates the token.
/// </summary>
public interface IGuestBookingCancellationService
{
    /// <summary>
    /// Generates a one-time cancel token, stores its SHA-256 hash on the booking with an expiry date, and
    /// emails the signed link to the guest.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">The booking is not in a cancellable state.</exception>
    Task SendCancelLinkAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the token, computes the refund from the property cancellation policy (base = BasePrice, no CasaZen
    /// floor, <see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>) and cancels
    /// the booking. The link is spent by the cancellation that succeeds, in the same transaction (its expiry becomes
    /// the moment of the cancellation): an error (a rule, a payment in progress, Stripe not reachable) leaves the link
    /// usable until it expires.
    /// </summary>
    /// <remarks>
    /// A booking that is already cancelled (by the host, by an earlier click of the link, or by a click that raced this
    /// one) answers with the outcome of that cancellation and refunds nothing again, however many times the link is used.
    /// </remarks>
    /// <returns>The cancellation result including the booking and any Stripe refunds initiated.</returns>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// Token invalid or expired, booking not cancellable, or a rule of the cancellation refused the refund (a payment
    /// changed between the decision and the lock: nothing is cancelled and the link stays usable, a new click decides again).
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">A payment is completing.</exception>
    Task<GuestCancellationResult> CancelByGuestLinkAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);
}

/// <summary>Result of a guest self-service cancellation.</summary>
/// <param name="Booking">The cancelled booking.</param>
/// <param name="Refunds">Refunds the cancellation created, as Stripe left them (may be empty when nothing was paid).</param>
/// <param name="RefundAmount">
/// The refund the cancellation decided: the policy's share of the base price, capped at what was refundable (0 when nothing
/// was paid online or the policy grants 0%). It is the decision, not Stripe's confirmation: a refund that Stripe has not
/// confirmed yet, or rejected, is in <paramref name="Refunds"/> with its status.
/// </param>
public sealed record GuestCancellationResult(
    Booking Booking,
    IReadOnlyList<PaymentRefund> Refunds,
    decimal RefundAmount);
