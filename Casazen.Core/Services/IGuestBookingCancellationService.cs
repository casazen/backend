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
    /// floor), cancels the booking and invalidates the token.
    /// </summary>
    /// <returns>The cancellation result including the booking and any Stripe refunds initiated.</returns>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Token invalid, expired, or booking not cancellable.</exception>
    /// <exception cref="Exceptions.DomainConflictException">Already cancelled or a payment is completing.</exception>
    Task<GuestCancellationResult> CancelByGuestLinkAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);
}

/// <summary>Result of a guest self-service cancellation.</summary>
/// <param name="Booking">The cancelled booking.</param>
/// <param name="Refunds">Stripe refunds initiated (may be empty when nothing was paid).</param>
/// <param name="RefundAmount">Amount actually refunded (0 when nothing was paid or policy grants 0%).</param>
public sealed record GuestCancellationResult(
    Booking Booking,
    IReadOnlyList<PaymentRefund> Refunds,
    decimal RefundAmount);
