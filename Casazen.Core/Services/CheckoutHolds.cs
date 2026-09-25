using System.Linq.Expressions;
using Casazen.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// Holds of the public checkout (BK-21, A3-13). <c>POST /api/public/bookings</c> stores a <see cref="BookingSource.Direct"/>
/// booking <see cref="BookingStatus.Pending"/> while the guest pays (PaymentIntent) or saves a card (SetupIntent). The
/// hold keeps its dates for <c>DirectBooking:PendingTtlMinutes</c> from its creation; after that it no longer occupies
/// them, and the <c>checkout-hold-expiry</c> job (or the next booking of the same dates) cancels it with its intent.
/// A "pay at the property" request (<see cref="PaymentOption.OnSite"/>, decision D5, BK-06) is a hold too, with its own
/// deadline (<see cref="Booking.RequestExpiresAt"/>, see <see cref="OnSiteRequests"/>).
/// <para>
/// Single definition of "expired hold": the expiry job, the cleanup before a new booking, public availability, the host
/// calendar, the iCal export and the overlap checks all use these expressions, never a copy of the rule.
/// </para>
/// </summary>
public static class CheckoutHolds
{
    public const string TtlMinutesSetting = "DirectBooking:PendingTtlMinutes";

    /// <summary>
    /// 30 minutes (BK-04, A3-04). The checkout pays a PaymentIntent with the Stripe Payment Element: there is no Checkout
    /// Session, no timer in the page and no expiry of the PaymentIntent on Stripe, so the hold is the only bound. 15 minutes
    /// did not cover 3-D Secure plus a banking app; 30 minutes is also the shortest lifetime Stripe allows for its own
    /// hosted Checkout Session (<c>expires_at</c>: 30 minutes to 24 hours). A payment that still arrives later is
    /// reconfirmed or refunded by the payment webhook. See docs/runbooks/stripe.md "Late payments".
    /// </summary>
    public const int DefaultTtlMinutes = 30;

    /// <summary>The configured hold duration in minutes (at least 1).</summary>
    public static int GetTtlMinutes(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(TtlMinutesSetting, DefaultTtlMinutes));

    /// <summary>Holds created before this instant are expired.</summary>
    public static DateTime ExpiryCutoffUtc(DateTime nowUtc, int ttlMinutes) => nowUtc.AddMinutes(-ttlMinutes);

    /// <summary>The instants that decide, at <paramref name="nowUtc"/>, which holds are expired.</summary>
    public static HoldExpiryCutoff CutoffAt(DateTime nowUtc, int ttlMinutes) =>
        new(nowUtc, ExpiryCutoffUtc(nowUtc, ttlMinutes));

    /// <summary>
    /// An expired hold at <paramref name="cutoff"/>: still <see cref="BookingStatus.Pending"/> and from the public checkout
    /// (<see cref="BookingSource.Direct"/>) — host bookings (<see cref="BookingSource.Manual"/>), OTA bookings and every
    /// other status are never holds — and either
    /// <list type="bullet">
    /// <item>a payment hold (not <see cref="PaymentOption.OnSite"/>) with a Stripe PaymentIntent or SetupIntent (a Pending
    /// booking without one is not a checkout hold), without a payment <see cref="PaymentStatus.Processing"/> or
    /// <see cref="PaymentStatus.Completed"/> (Stripe said the guest has paid or is paying: the expiry records it, the
    /// webhook confirms the booking, so the dates stay taken), created before <see cref="HoldExpiryCutoff.CheckoutCreatedBeforeUtc"/>;</item>
    /// <item>or a "pay at the property" request (<see cref="PaymentOption.OnSite"/>, D5) past its own deadline
    /// <see cref="Booking.RequestExpiresAt"/>: the guest did not confirm the email in time, or the host did not answer in
    /// time (<see cref="OnSiteRequests"/>). A request without a deadline (created before BK-06) expires like a payment
    /// hold, with the checkout TTL.</item>
    /// </list>
    /// </summary>
    public static Expression<Func<Booking, bool>> IsExpired(HoldExpiryCutoff cutoff)
    {
        var nowUtc = cutoff.NowUtc;
        var checkoutCreatedBeforeUtc = cutoff.CheckoutCreatedBeforeUtc;
        return b => b.Status == BookingStatus.Pending &&
                    b.Source == BookingSource.Direct &&
                    ((b.PaymentOption != PaymentOption.OnSite &&
                      (b.StripeSetupIntentId != null || b.Payments.Any(p => p.StripePaymentIntentId != null)) &&
                      !b.Payments.Any(p => p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Completed) &&
                      b.CreatedAt < checkoutCreatedBeforeUtc) ||
                     (b.PaymentOption == PaymentOption.OnSite &&
                      ((b.RequestExpiresAt != null && b.RequestExpiresAt < nowUtc) ||
                       (b.RequestExpiresAt == null && b.CreatedAt < checkoutCreatedBeforeUtc))));
    }

    /// <summary>
    /// A booking that takes its dates: not cancelled and, when <paramref name="expiredHoldCutoff"/> is given, not an
    /// expired hold (<see cref="IsExpired"/>). Without a cutoff every booking that is not cancelled counts: the final check
    /// before an insert stays strict, so the dates of a hold whose guest paid late are never given away.
    /// </summary>
    public static Expression<Func<Booking, bool>> OccupiesDates(HoldExpiryCutoff? expiredHoldCutoff)
    {
        Expression<Func<Booking, bool>> notCancelled = b => b.Status != BookingStatus.Cancelled;
        if (expiredHoldCutoff is not { } cutoff)
            return notCancelled;

        var booking = notCancelled.Parameters[0];
        var expired = IsExpired(cutoff);
        var expiredBody = new ParameterReplacer(expired.Parameters[0], booking).Visit(expired.Body);
        return Expression.Lambda<Func<Booking, bool>>(
            Expression.AndAlso(notCancelled.Body, Expression.Not(expiredBody)),
            booking);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>When holds are judged (<see cref="CheckoutHolds.IsExpired"/>).</summary>
/// <param name="NowUtc">Now: a "pay at the property" request past its <see cref="Booking.RequestExpiresAt"/> is expired.</param>
/// <param name="CheckoutCreatedBeforeUtc">A payment hold created before this instant (now − checkout TTL) is expired.</param>
public readonly record struct HoldExpiryCutoff(DateTime NowUtc, DateTime CheckoutCreatedBeforeUtc);
