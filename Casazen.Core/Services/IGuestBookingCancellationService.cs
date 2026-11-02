using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest self-service cancellation (BK-02, BK-07, PO 2026-10-08, DB-08a), from two doors that end in the same cancellation:
/// <list type="bullet">
/// <item>the <b>guest area</b>: the guest who proved the booking with its code and email (<see cref="IGuestSessionService"/>) sees what
///   they would get back and cancels, with no email link in between (the PO's choice, DB-D3);</item>
/// <item>the <b>signed one-time link</b> the host emailed to the guest, whose page shows the same preview.</item>
/// </list>
/// The refund is computed exclusively from the property's <see cref="CancellationPolicy"/> and the tourist tax
/// (<see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>, the one calculation): the CasaZen
/// floor (<see cref="Booking.FreeRefundDeadline"/>) does not apply to guest-initiated cancellations. The preview and the cancellation
/// use that same function, so the amount shown is the amount refunded; a cancellation that carries the amount the guest was shown
/// refuses to go on when it is no longer the same (<see cref="GuestRefundChangedException"/>).
/// Callers do not check authorisation before calling the guest-area methods: the booking id comes from the guest session
/// (<see cref="IGuestSessionService.FindBookingAsync"/>, which answers the uniform 404 for a booking that is not the guest's); the
/// methods with a token verify it.
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
    /// What cancelling would do now, for the guest of the session who is in the guest area (<see cref="GuestCancellationChannel.GuestArea"/>:
    /// a stay that has started is not cancellable here). Nothing is changed.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    Task<GuestCancellationPreview> PreviewForGuestAreaAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same preview for the page of the signed link: the token is verified (and not spent) like the cancellation does, so a link
    /// that does not open the cancellation does not open the preview either.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Token invalid or expired.</exception>
    Task<GuestCancellationPreview> PreviewByGuestLinkAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest of the session cancels from the guest area, for the refund they were shown. The refund is worked out again; if it is not
    /// <paramref name="expectedRefundAmount"/> (a threshold of the policy was crossed, a payment changed) nothing is cancelled and
    /// <see cref="GuestRefundChangedException"/> carries the new preview. Otherwise the booking is cancelled through
    /// <see cref="IBookingCancellationService"/> (dates freed, intents canceled, refund reserved and sent to Stripe: it is
    /// <i>pending</i> until Stripe confirms it). A booking that is already cancelled answers with the outcome of that cancellation and
    /// refunds nothing again, however many times this is called (idempotent).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">The stay has started or the booking is not in a cancellable state.</exception>
    /// <exception cref="Exceptions.DomainConflictException">A payment is completing, or <see cref="GuestRefundChangedException"/>.</exception>
    Task<GuestCancellationResult> CancelFromGuestAreaAsync(
        Guid bookingId,
        decimal expectedRefundAmount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the token, computes the refund from the property cancellation policy (base = BasePrice and the tourist tax, no CasaZen
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

    /// <summary>
    /// The cancellation of the signed link for the refund the guest was shown on its page (DB-08a): as
    /// <see cref="CancelFromGuestAreaAsync"/>, a refund that is no longer <paramref name="expectedRefundAmount"/> cancels nothing and
    /// leaves the link usable (<see cref="GuestRefundChangedException"/>).
    /// </summary>
    Task<GuestCancellationResult> CancelByGuestLinkAsync(
        Guid bookingId,
        string token,
        decimal expectedRefundAmount,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of a guest self-service cancellation.</summary>
/// <param name="Booking">The cancelled booking.</param>
/// <param name="Refunds">Refunds the cancellation created, as Stripe left them (may be empty when nothing was paid).</param>
/// <param name="RefundAmount">
/// The refund the cancellation decided: the policy's share of the base price and the tourist tax, capped at what was refundable (0 when
/// nothing was paid online or the policy and the tax give nothing). It is the decision, not Stripe's confirmation: a refund that Stripe
/// has not confirmed yet, or rejected, is in <paramref name="Refunds"/> with its status.
/// </param>
public sealed record GuestCancellationResult(
    Booking Booking,
    IReadOnlyList<PaymentRefund> Refunds,
    decimal RefundAmount)
{
    /// <summary>Where the refund stands: <see cref="GuestCancellationRefundStatuses"/>, never "confirmed" before Stripe says so.</summary>
    public string RefundStatus => GuestCancellationRefundStatuses.Of(Refunds);
}
