using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Records a paid Openapi filing fee. The amount must match the pending row of that lease and org; a mismatch grants nothing.
/// </summary>
public sealed class OpenapiRegistrationPaymentHandler(AppDbContext db, ILogger<OpenapiRegistrationPaymentHandler> logger)
{
    public static bool IsPayment(PaymentIntent? paymentIntent) =>
        paymentIntent?.Metadata is { } metadata
        && metadata.TryGetValue("kind", out var kind)
        && string.Equals(kind, OpenapiRegistrationFee.StripeKind, StringComparison.Ordinal);

    public async Task HandlePaymentIntentSucceededAsync(PaymentIntent paymentIntent, DateTime? paidAt = null)
    {
        if (!IsPayment(paymentIntent))
            return;

        var metadata = paymentIntent.Metadata;
        if (!Guid.TryParse(metadata.GetValueOrDefault("leaseId"), out var leaseId)
            || !Guid.TryParse(metadata.GetValueOrDefault("orgId"), out var orgId)
            || !string.Equals(paymentIntent.Currency, "eur", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("OpenAPI registration payment {PaymentIntentId} is missing lease, org or currency", paymentIntent.Id);
            return;
        }

        var amount = (int)paymentIntent.Amount;
        var payment = await db.OpenapiLeaseRegistrationPayments
            .Where(p => p.LeaseContractId == leaseId && p.OrgId == orgId && p.AmountCents == amount)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (payment is null)
        {
            logger.LogError(
                "OpenAPI registration payment {PaymentIntentId} matches no quote for LeaseId={LeaseId} OrgId={OrgId} AmountCents={AmountCents}",
                paymentIntent.Id,
                leaseId,
                orgId,
                amount);
            return;
        }

        if (payment.Status == OpenapiRegistrationPaymentStatus.Completed)
            return;

        payment.Status = OpenapiRegistrationPaymentStatus.Completed;
        payment.PaidAt = DateTime.SpecifyKind(paidAt ?? paymentIntent.Created, DateTimeKind.Utc);
        payment.StripePaymentIntentId = paymentIntent.Id;
        await db.SaveChangesAsync();
        logger.LogInformation(
            "OpenAPI registration fee paid. LeaseId={LeaseId} OrgId={OrgId} FeePercent={FeePercent} AmountCents={AmountCents}",
            leaseId,
            orgId,
            payment.FeePercent,
            payment.AmountCents);
    }
}
