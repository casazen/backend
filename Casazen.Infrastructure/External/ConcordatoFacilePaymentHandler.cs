using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Applies the Stripe events of the ConcordatoFacile one-time payment to <see cref="ConcordatoFacilePurchase"/> (ADR-005).
/// <see cref="StripeWebhookHandler"/> has already verified the signature, claimed the event in <c>ProcessedStripeEvents</c>
/// (source <see cref="WebhookSource.ConcordatoFacile"/>, ADR-005-R5/R6) and opened the transaction these updates share: a
/// duplicate delivery never gets here, and a failure rolls the claim back so Stripe's retry processes the event again.
/// </summary>
public class ConcordatoFacilePaymentHandler(
    AppDbContext dbContext,
    ILogger<ConcordatoFacilePaymentHandler> logger,
    IStripeClient? stripeClient = null)
{
    /// <summary>The price of the service: EUR 119,00 (ADR-005-R4).</summary>
    public const long PriceCents = 11900;

    /// <summary>The only currency of the service (ADR-005-R4).</summary>
    public const string Currency = "eur";

    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    /// <summary>
    /// <c>payment_intent.succeeded</c> of a CF purchase (ADR-005-R4, R5): the payment grants access only when it is exactly
    /// <see cref="PriceCents"/> in <see cref="Currency"/>. <paramref name="paidAt"/> is the time of the event (an async
    /// payment method settles days after the PaymentIntent was created); without it the PaymentIntent's own time is used.
    /// </summary>
    public async Task HandlePaymentIntentSucceededAsync(PaymentIntent paymentIntent, DateTime? paidAt = null)
    {
        var metadata = paymentIntent.Metadata;
        var leaseRaw = metadata is not null && metadata.TryGetValue("leaseId", out var leaseValue) ? leaseValue : null;
        var orgRaw = metadata is not null && metadata.TryGetValue("orgId", out var orgValue) ? orgValue : null;

        // ADR-005-R4: the amount and the currency are checked before anything is granted; a mismatch is an error for the operators.
        if (paymentIntent.Amount != PriceCents || !string.Equals(paymentIntent.Currency, Currency, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "ConcordatoFacile payment intent {PaymentIntentId} has the wrong amount ({Amount} {PaymentCurrency}, expected {PriceCents} {ExpectedCurrency}) for orgId={OrgId}, leaseId={LeaseId}: access NOT granted",
                paymentIntent.Id,
                paymentIntent.Amount,
                paymentIntent.Currency,
                PriceCents,
                Currency,
                orgRaw ?? "unknown",
                leaseRaw ?? "unknown");
            return;
        }

        if (!Guid.TryParse(leaseRaw, out var leaseId))
        {
            logger.LogWarning("ConcordatoFacile payment intent {PaymentIntentId} has no valid leaseId in its metadata", paymentIntent.Id);
            return;
        }

        var purchase = await FindPurchaseAsync(paymentIntent.Id, leaseId);
        if (purchase is null)
        {
            logger.LogWarning(
                "No open ConcordatoFacilePurchase for payment intent {PaymentIntentId} (leaseId={LeaseId})",
                paymentIntent.Id,
                leaseId);
            return;
        }

        // The tenant of the payment (when the checkout wrote it) must be the tenant of the purchase.
        if (Guid.TryParse(orgRaw, out var orgId) && orgId != purchase.OrgId)
        {
            logger.LogError(
                "ConcordatoFacile payment intent {PaymentIntentId} names org {OrgId} but the purchase {PurchaseId} belongs to org {PurchaseOrgId}: access NOT granted",
                paymentIntent.Id,
                orgId,
                purchase.Id,
                purchase.OrgId);
            return;
        }

        if (purchase.Status == ConcordatoFacilePurchaseStatus.Completed)
        {
            logger.LogInformation(
                "ConcordatoFacile purchase {PurchaseId} is already completed (payment intent {PaymentIntentId}): nothing to do",
                purchase.Id,
                paymentIntent.Id);
            return;
        }

        // ADR-005-R5: Completed, PaidAt, and the PaymentIntent that the purchase did not know until now.
        purchase.Status = ConcordatoFacilePurchaseStatus.Completed;
        purchase.PaidAt = ToUtc(paidAt ?? paymentIntent.Created);
        purchase.StripePaymentIntentId = paymentIntent.Id;
        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "ConcordatoFacile purchase {PurchaseId} completed for lease {LeaseId} (payment intent {PaymentIntentId})",
            purchase.Id,
            leaseId,
            paymentIntent.Id);
    }

    /// <summary>
    /// <c>payment_intent.payment_failed</c> and <c>payment_intent.canceled</c> of a CF purchase (CF-BE, closes CF-FIX-FU2): the purchase is
    /// closed (<c>Failed</c>) only when its Checkout Session can no longer succeed, which Stripe says and the event does not. A card
    /// declined while the Session is still open fails the PaymentIntent but the customer can try another card: the purchase stays pending.
    /// A Session that expired (Stripe cancels its PaymentIntent) or that was completed with a delayed payment method that then failed
    /// cannot be paid any more: the purchase is closed and a new checkout is possible. A purchase that is not pending (paid, refunded,
    /// already closed) is never touched. The payment of a closed purchase that arrives late still completes it
    /// (<see cref="HandlePaymentIntentSucceededAsync"/> also looks at the <c>Failed</c> ones).
    /// </summary>
    public async Task HandlePaymentNotCompletedAsync(PaymentIntent paymentIntent, string eventType)
    {
        ArgumentNullException.ThrowIfNull(paymentIntent);

        var metadata = paymentIntent.Metadata;
        var orgRaw = metadata is not null && metadata.TryGetValue("orgId", out var orgValue) ? orgValue : null;

        // The Session of this PaymentIntent, from Stripe: the event does not say whether the customer can still try again.
        var sessions = await new SessionService(Client).ListAsync(
            new SessionListOptions { PaymentIntent = paymentIntent.Id, Limit = 1 });
        var session = sessions.Data.FirstOrDefault();
        if (session is null)
        {
            logger.LogInformation(
                "ConcordatoFacile {EventType}: no Checkout Session for payment intent {PaymentIntentId}, nothing closed",
                eventType,
                paymentIntent.Id);
            return;
        }

        var purchase = await dbContext.ConcordatoFacilePurchases
            .FirstOrDefaultAsync(p => p.StripeCheckoutSessionId == session.Id);
        if (purchase is null)
        {
            logger.LogInformation(
                "ConcordatoFacile {EventType}: no purchase for Checkout Session {SessionId} (payment intent {PaymentIntentId})",
                eventType,
                session.Id,
                paymentIntent.Id);
            return;
        }

        // The tenant of the payment (when the checkout wrote it) must be the tenant of the purchase.
        if (Guid.TryParse(orgRaw, out var orgId) && orgId != purchase.OrgId)
        {
            logger.LogError(
                "ConcordatoFacile payment intent {PaymentIntentId} names org {OrgId} but the purchase {PurchaseId} belongs to org {PurchaseOrgId}: purchase NOT closed",
                paymentIntent.Id,
                orgId,
                purchase.Id,
                purchase.OrgId);
            return;
        }

        if (purchase.Status != ConcordatoFacilePurchaseStatus.Pending)
        {
            logger.LogInformation(
                "ConcordatoFacile purchase {PurchaseId} is {Status}: {EventType} changes nothing",
                purchase.Id,
                purchase.Status,
                eventType);
            return;
        }

        if (!CanNoLongerSucceed(session))
        {
            logger.LogInformation(
                "ConcordatoFacile purchase {PurchaseId}: {EventType} but Checkout Session {SessionId} is {SessionStatus} ({PaymentStatus}), the customer can still pay: purchase stays pending",
                purchase.Id,
                eventType,
                session.Id,
                session.Status,
                session.PaymentStatus);
            return;
        }

        purchase.Status = ConcordatoFacilePurchaseStatus.Failed;
        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "ConcordatoFacile purchase {PurchaseId} closed for lease {LeaseId}: {EventType}, Checkout Session {SessionId} is {SessionStatus}",
            purchase.Id,
            purchase.LeaseContractId,
            eventType,
            session.Id,
            session.Status);
    }

    /// <summary>
    /// Whether a Checkout Session can no longer lead to a payment: it expired, or it was completed but not paid (a delayed payment
    /// method whose payment failed or was canceled: a completed Session cannot be tried again). An open Session can: the customer can
    /// try another card. A paid one is paid.
    /// </summary>
    internal static bool CanNoLongerSucceed(Session session) => session.Status switch
    {
        "expired" => true,
        "complete" => !string.Equals(session.PaymentStatus, "paid", StringComparison.Ordinal)
            && !string.Equals(session.PaymentStatus, "no_payment_required", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// <c>charge.refunded</c> of a CF purchase (ADR-005-R7): a full refund marks the purchase Refunded. The practice and the
    /// documents of the lease are never touched here, and a partial refund leaves the status alone.
    /// </summary>
    public async Task HandleRefundAsync(Charge charge)
    {
        if (string.IsNullOrWhiteSpace(charge.PaymentIntentId))
            return;

        var purchase = await dbContext.ConcordatoFacilePurchases
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == charge.PaymentIntentId);
        if (purchase is null)
        {
            logger.LogInformation(
                "No ConcordatoFacilePurchase for the refunded charge {ChargeId} (payment intent {PaymentIntentId})",
                charge.Id,
                charge.PaymentIntentId);
            return;
        }

        if (!charge.Refunded)
        {
            logger.LogInformation(
                "Partial refund of the charge {ChargeId} of ConcordatoFacile purchase {PurchaseId}: status unchanged",
                charge.Id,
                purchase.Id);
            return;
        }

        if (purchase.Status == ConcordatoFacilePurchaseStatus.Refunded)
            return;

        purchase.Status = ConcordatoFacilePurchaseStatus.Refunded;
        purchase.RefundedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "ConcordatoFacile purchase {PurchaseId} refunded for lease {LeaseId} (payment intent {PaymentIntentId})",
            purchase.Id,
            purchase.LeaseContractId,
            charge.PaymentIntentId);
    }

    /// <summary>
    /// The purchase a payment settles: the one already bound to the PaymentIntent, else the newest open one of the lease
    /// (Pending, or Failed when the customer paid on retry); a lease can also carry an earlier refunded purchase.
    /// </summary>
    private async Task<ConcordatoFacilePurchase?> FindPurchaseAsync(string paymentIntentId, Guid leaseId)
    {
        var bound = await dbContext.ConcordatoFacilePurchases
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == paymentIntentId);
        if (bound is not null)
            return bound;

        return await dbContext.ConcordatoFacilePurchases
            .Where(p => p.LeaseContractId == leaseId
                && (p.Status == ConcordatoFacilePurchaseStatus.Pending || p.Status == ConcordatoFacilePurchaseStatus.Failed))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
    }

    private static DateTime ToUtc(DateTime value) =>
        value == default ? DateTime.UtcNow : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
