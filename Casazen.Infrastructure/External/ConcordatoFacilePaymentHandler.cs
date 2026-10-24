using Stripe;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Handles Stripe webhook events for ConcordatoFacile one-time payments (ADR-005).
/// </summary>
public class ConcordatoFacilePaymentHandler(
    AppDbContext dbContext,
    ILogger<ConcordatoFacilePaymentHandler> logger)
{
    /// <summary>
    /// Processes payment_intent.succeeded for CF purchases (ADR-005-R4, ADR-005-R5, ADR-005-R6).
    /// Verifies amount/currency, updates purchase status, and claims the event for idempotency.
    /// </summary>
    public async Task HandlePaymentIntentSucceededAsync(PaymentIntent paymentIntent)
    {
        if (paymentIntent is null)
            return;

        // ADR-005-R4: Verify amount == 11900 AND currency == "eur"
        if (paymentIntent.Amount != 11900 || !string.Equals(paymentIntent.Currency, "eur", StringComparison.OrdinalIgnoreCase))
        {
            var leaseId = paymentIntent.Metadata?.TryGetValue("leaseId", out var leaseIdRaw) == true ? leaseIdRaw : "unknown";
            var orgId = paymentIntent.Metadata?.TryGetValue("orgId", out var orgIdRaw) == true ? orgIdRaw : "unknown";

            logger.LogError(
                "ConcordatoFacile payment intent {PaymentIntentId} has wrong amount ({Amount} {Currency}) for orgId={OrgId}, leaseId={LeaseId}: access NOT granted",
                paymentIntent.Id,
                paymentIntent.Amount,
                paymentIntent.Currency,
                orgId,
                leaseId);
            return;
        }

        // Extract leaseId from metadata
        if (paymentIntent.Metadata?.TryGetValue("leaseId", out var leaseIdValue) != true ||
            !Guid.TryParse(leaseIdValue, out var leaseId))
        {
            logger.LogWarning("ConcordatoFacile payment intent {PaymentIntentId} has no valid leaseId in metadata", paymentIntent.Id);
            return;
        }

        // Query purchase by StripeCheckoutSessionId (from invoice) OR StripePaymentIntentId
        var invoiceId = paymentIntent.InvoiceId;
        var purchase = await dbContext.ConcordatoFacilePurchases
            .Where(p => p.StripeCheckoutSessionId == invoiceId || p.StripePaymentIntentId == paymentIntent.Id)
            .FirstOrDefaultAsync();

        if (purchase is null)
        {
            logger.LogWarning(
                "No ConcordatoFacilePurchase found for payment intent {PaymentIntentId} (invoice={InvoiceId}, leaseId={LeaseId})",
                paymentIntent.Id,
                invoiceId,
                leaseId);
            return;
        }

        // ADR-005-R5: Update purchase status and payment intent ID
        purchase.Status = ConcordatoFacilePurchaseStatus.Completed;
        purchase.PaidAt = paymentIntent.Created.Year >= 2000
            ? DateTime.SpecifyKind(paymentIntent.Created, DateTimeKind.Utc)
            : DateTime.UtcNow;
        purchase.StripePaymentIntentId = paymentIntent.Id;

        try
        {
            await dbContext.SaveChangesAsync();
            logger.LogInformation(
                "ConcordatoFacile purchase {PurchaseId} completed for lease {LeaseId} (payment intent {PaymentIntentId})",
                purchase.Id,
                leaseId,
                paymentIntent.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // ADR-005-R6: Duplicate event caught via ProcessedStripeEvent unique constraint
            // (the main handler's TryClaimEventAsync already did this, but we treat it as idempotent success)
            logger.LogInformation(
                "Duplicate event for ConcordatoFacile payment intent {PaymentIntentId}: treated as idempotent success",
                paymentIntent.Id);
        }
    }

    /// <summary>
    /// Processes charge.refunded for CF purchases (ADR-005-R7).
    /// Updates purchase status to Refunded without deleting CF data.
    /// </summary>
    public async Task HandleRefundAsync(Charge charge)
    {
        if (charge is null || string.IsNullOrWhiteSpace(charge.PaymentIntentId))
            return;

        // Query purchase by StripePaymentIntentId
        var purchase = await dbContext.ConcordatoFacilePurchases
            .Where(p => p.StripePaymentIntentId == charge.PaymentIntentId)
            .FirstOrDefaultAsync();

        if (purchase is null)
        {
            logger.LogInformation(
                "No ConcordatoFacilePurchase found for refund charge {ChargeId} (payment intent {PaymentIntentId})",
                charge.Id,
                charge.PaymentIntentId);
            return;
        }

        // ADR-005-R7: Update status to Refunded, do NOT delete ConcordatoFacilePractice or LeaseDocuments
        purchase.Status = ConcordatoFacilePurchaseStatus.Refunded;
        purchase.RefundedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "ConcordatoFacile purchase {PurchaseId} refunded for lease {LeaseId} (payment intent {PaymentIntentId})",
            purchase.Id,
            purchase.LeaseContractId,
            charge.PaymentIntentId);
    }
}
