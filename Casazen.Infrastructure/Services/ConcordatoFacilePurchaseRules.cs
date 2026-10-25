using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// When a ConcordatoFacile purchase is over (CF-BE, closes CF-IMPORT-FU1 and CF-FIX-FU2). A purchase is <c>Pending</c> from the moment
/// the Stripe Checkout Session is created; the session is created with an explicit expiry, so a purchase still <c>Pending</c> after
/// that expiry and a margin can no longer be paid through that session: it is closed. A closed purchase (<c>Failed</c>, or a
/// <c>Pending</c> that old) is not "already purchased", is not reused, and does not keep a draft from being deleted. The states are the
/// existing ones: <c>Failed</c> is documented as "payment failed or checkout session expired", so no migration is needed.
/// </summary>
public static class ConcordatoFacilePurchaseRules
{
    /// <summary>
    /// How long a Checkout Session of the service stays open, set at its creation (<c>expires_at</c>). Stripe accepts 30 minutes to 24
    /// hours and, left alone, uses 24 hours.
    /// </summary>
    public static readonly TimeSpan CheckoutSessionLifetime = TimeSpan.FromHours(2);

    /// <summary>The time added to the lifetime before a purchase still pending counts as closed (clocks, the delay of the expiry event).</summary>
    public static readonly TimeSpan ClosingMargin = TimeSpan.FromMinutes(10);

    /// <summary>A purchase created before this instant and still <c>Pending</c> is closed.</summary>
    public static DateTime PendingClosedBefore(DateTime utcNow) => utcNow - (CheckoutSessionLifetime + ClosingMargin);

    /// <summary>
    /// The purchases that still count (EF-translatable): not <c>Failed</c>, and not a <c>Pending</c> older than
    /// <see cref="PendingClosedBefore"/>. Completed and refunded purchases count whatever their age.
    /// </summary>
    public static Expression<Func<ConcordatoFacilePurchase, bool>> IsOpen(DateTime utcNow)
    {
        var closedBefore = PendingClosedBefore(utcNow);
        return purchase => purchase.Status != ConcordatoFacilePurchaseStatus.Failed
            && !(purchase.Status == ConcordatoFacilePurchaseStatus.Pending && purchase.CreatedAt < closedBefore);
    }
}
