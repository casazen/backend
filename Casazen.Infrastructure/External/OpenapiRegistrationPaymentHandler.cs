using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Records a paid Openapi filing fee from the platform webhook only, and only for the Checkout session that row stores.
/// A Connect PaymentIntent with the same kind, lease, org and amount is the host's charge: it does not pay this fee.
/// A late success completes that session's Pending row and no other.
/// </summary>
public sealed class OpenapiRegistrationPaymentHandler(
    AppDbContext db,
    ILogger<OpenapiRegistrationPaymentHandler> logger,
    Stripe.IStripeClient? stripeClient = null)
{
    private Stripe.IStripeClient Client => stripeClient ?? Stripe.StripeConfiguration.StripeClient;

    public static bool IsPayment(PaymentIntent? paymentIntent) =>
        paymentIntent?.Metadata is { } metadata
        && metadata.TryGetValue("kind", out var kind)
        && string.Equals(kind, OpenapiRegistrationFee.StripeKind, StringComparison.Ordinal);

    public async Task HandlePaymentIntentSucceededAsync(
        PaymentIntent paymentIntent,
        WebhookSource source,
        DateTime? paidAt = null)
    {
        if (source != WebhookSource.Platform || !IsPayment(paymentIntent))
        {
            logger.LogInformation(
                "OpenAPI registration payment ignored. PaymentIntentId={PaymentIntentId} Source={Source}",
                paymentIntent.Id,
                source);
            return;
        }

        var metadata = paymentIntent.Metadata;
        if (!Guid.TryParse(metadata.GetValueOrDefault("leaseId"), out var leaseId)
            || !Guid.TryParse(metadata.GetValueOrDefault("orgId"), out var orgId)
            || !string.Equals(paymentIntent.Currency, "eur", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("OpenAPI registration payment {PaymentIntentId} is missing lease, org or currency", paymentIntent.Id);
            return;
        }

        var sessionId = await CheckoutSessionIdAsync(paymentIntent.Id);
        if (sessionId is null)
        {
            logger.LogWarning(
                "OpenAPI registration payment {PaymentIntentId} is not a stored Checkout session",
                paymentIntent.Id);
            return;
        }

        var payment = await db.OpenapiLeaseRegistrationPayments
            .FirstOrDefaultAsync(p => p.StripeCheckoutSessionId == sessionId
                && p.LeaseContractId == leaseId
                && p.OrgId == orgId);
        if (payment is null)
        {
            logger.LogError(
                "OpenAPI registration payment {PaymentIntentId} matches no row for session {SessionId}",
                paymentIntent.Id,
                sessionId);
            return;
        }

        if (payment.Status != OpenapiRegistrationPaymentStatus.Pending)
        {
            logger.LogInformation(
                "OpenAPI registration payment {PaymentId} is {Status}: a late success changes nothing",
                payment.Id,
                payment.Status);
            return;
        }

        if (payment.AmountCents != (int)paymentIntent.Amount)
        {
            logger.LogError(
                "OpenAPI registration payment {PaymentIntentId} amount {Amount} does not match session {SessionId}",
                paymentIntent.Id,
                paymentIntent.Amount,
                sessionId);
            return;
        }

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

    /// <summary>The Checkout session of this PaymentIntent on the platform account, or null when it is not one of ours.</summary>
    private async Task<string?> CheckoutSessionIdAsync(string paymentIntentId)
    {
        var list = await new SessionService(Client).ListAsync(
            new SessionListOptions { PaymentIntent = paymentIntentId, Limit = 1 });
        return list.Data.FirstOrDefault()?.Id;
    }
}
