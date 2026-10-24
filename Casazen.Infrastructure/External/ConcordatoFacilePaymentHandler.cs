using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Applies the Stripe events of the ConcordatoFacile one-time payment to <see cref="ConcordatoFacilePurchase"/> (ADR-005).
/// <see cref="StripeWebhookHandler"/> has already verified the signature, claimed the event in <c>ProcessedStripeEvents</c>
/// (source <see cref="WebhookSource.ConcordatoFacile"/>, ADR-005-R5/R6) and opened the transaction these updates share: a
/// duplicate delivery never gets here, and a failure rolls the claim back so Stripe's retry processes the event again.
/// </summary>
public class ConcordatoFacilePaymentHandler(
    AppDbContext dbContext,
    ILogger<ConcordatoFacilePaymentHandler> logger)
{
    /// <summary>The price of the service: EUR 119,00 (ADR-005-R4).</summary>
    public const long PriceCents = 11900;

    /// <summary>The only currency of the service (ADR-005-R4).</summary>
    public const string Currency = "eur";

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
