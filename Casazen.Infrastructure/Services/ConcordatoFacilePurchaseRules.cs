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
    public static readonly TimeSpan CheckoutSessionLifetime = TimeSpan.FromHours(2);

    public static readonly TimeSpan ClosingMargin = TimeSpan.FromMinutes(10);

    public static DateTime PendingClosedBefore(DateTime utcNow) => utcNow - (CheckoutSessionLifetime + ClosingMargin);

    public static Expression<Func<ConcordatoFacilePurchase, bool>> IsOpen(DateTime utcNow)
    {
        var closedBefore = PendingClosedBefore(utcNow);
        return purchase => purchase.Status != ConcordatoFacilePurchaseStatus.Failed
            && !(purchase.Status == ConcordatoFacilePurchaseStatus.Pending && purchase.CreatedAt < closedBefore);
    }
}
