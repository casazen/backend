using Stripe;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class StripeWebhookHandler(
    IPaymentRepository paymentRepository,
    IBookingRepository bookingRepository,
    IConnectOnboardingService connectOnboardingService,
    AppDbContext dbContext,
    IStripeBillingService stripeBillingService,
    IEntitlementService entitlementService,
    IVatCalculationService vatCalculationService,
    IOssRevenueTracker ossRevenueTracker,
    ISdiEInvoiceService sdiEInvoiceService,
    IRentBillingService rentBillingService,
    ILogger<StripeWebhookHandler> logger)
{
    private const string DirectBookingKind = "direct-booking";
    private const string RentChargeKind = "rent-charge";

    public Task HandleEventAsync(Event stripeEvent) =>
        HandleEventAsync(stripeEvent, WebhookSource.Platform);

    public async Task HandleEventAsync(Event stripeEvent, WebhookSource source)
    {
        // Claim the event slot FIRST — unique PK on EventId prevents concurrent workers
        // from processing the same event twice. If the insert fails (duplicate), skip.
        if (!await TryClaimEventAsync(stripeEvent, source))
        {
            logger.LogInformation("Skipping duplicate Stripe event {EventId}", stripeEvent.Id);
            return;
        }

        try
        {
            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    await HandlePaymentSucceededAsync(stripeEvent.Data.Object as PaymentIntent, source);
                    break;
                case "payment_intent.payment_failed":
                case "payment_intent.canceled":
                    await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent, source, stripeEvent.Type);
                    break;
                case "charge.refunded":
                    if (source == WebhookSource.Platform)
                        await HandleRefundAsync(stripeEvent.Data.Object as Charge);
                    break;
                case "account.updated":
                    if (source == WebhookSource.Connected)
                        await HandleAccountUpdatedAsync(stripeEvent.Data.Object as Account);
                    break;
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                    if (source == WebhookSource.Platform)
                        await HandleSubscriptionChangedAsync(stripeEvent.Data.Object as Subscription, stripeEvent.Type);
                    break;
                case "invoice.paid":
                    if (source == WebhookSource.Platform)
                        await HandleInvoicePaidAsync(stripeEvent.Data.Object as Invoice);
                    break;
                case "invoice.payment_failed":
                    if (source == WebhookSource.Platform)
                        await HandleInvoicePaymentFailedAsync(stripeEvent.Data.Object as Invoice);
                    break;
                default:
                    logger.LogInformation("Unhandled Stripe event: {EventType} (source={Source})", stripeEvent.Type, source);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Stripe webhook");
            throw;
        }
    }

    // Returns false if the event was already processed (duplicate); true if claimed successfully.
    // Inserts the idempotency record before business logic so concurrent Hangfire workers cannot
    // both pass the guard. The unique PK on EventId is the enforcement mechanism.
    // Events with no Id (e.g. synthetic test events) are always processed.
    private async Task<bool> TryClaimEventAsync(Event stripeEvent, WebhookSource source)
    {
        if (string.IsNullOrEmpty(stripeEvent.Id))
            return true;

        dbContext.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            EventId = stripeEvent.Id,
            EventType = stripeEvent.Type,
            Source = source,
            ProcessedAt = DateTime.UtcNow,
        });

        try
        {
            await dbContext.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
                                           || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task HandleSubscriptionChangedAsync(Subscription? subscription, string eventType)
    {
        if (subscription is null)
            return;

        var org = await ResolveOrgForSubscriptionAsync(subscription);
        if (org is null)
        {
            logger.LogError("No org resolved for subscription {SubscriptionId}", subscription.Id);
            return;
        }

        org.SubscriptionId = subscription.Id;
        org.SubscriptionStatus = MapSubscriptionStatus(subscription.Status, eventType);
        org.CurrentPeriodEnd = subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd;

        if (org.SubscriptionStatus is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
            org.PastDueSince = null;
        else if (org.SubscriptionStatus == SubscriptionStatus.PastDue && org.PastDueSince is null)
            org.PastDueSince = DateTime.UtcNow;

        var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
        var tier = stripeBillingService.MapPriceIdToTier(priceId);
        if (tier.HasValue)
            org.PlanTier = tier.Value;

        if (eventType == "customer.subscription.deleted")
            org.SubscriptionStatus = SubscriptionStatus.Canceled;

        if (!string.IsNullOrWhiteSpace(subscription.CustomerId))
            org.StripeCustomerId ??= subscription.CustomerId;

        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);
    }

    private async Task HandleInvoicePaidAsync(Invoice? invoice)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Id))
            return;

        if (await dbContext.PlatformInvoices.AnyAsync(i => i.StripeInvoiceId == invoice.Id))
            return;

        var org = await ResolveOrgForInvoiceAsync(invoice);
        if (org is null)
        {
            logger.LogError("No org resolved for invoice {InvoiceId}", invoice.Id);
            return;
        }

        if (invoice.Parent?.SubscriptionDetails?.SubscriptionId is not null)
        {
            org.SubscriptionStatus = SubscriptionStatus.Active;
            org.PastDueSince = null;
            org.UpdatedAt = DateTime.UtcNow;
        }

        var amountExVat = ConvertCentsToDecimal(invoice.SubtotalExcludingTax ?? invoice.Subtotal);
        var totalAmount = ConvertCentsToDecimal(invoice.Total);
        var viesValidated = org.VatIdValidatedAt.HasValue;
        var ossThreshold = await ossRevenueTracker.IsOssThresholdReachedAsync();
        var vatResult = vatCalculationService.Calculate(
            amountExVat,
            org.BillingCountry ?? "IT",
            org.VatId,
            viesValidated,
            ossThreshold);

        if (vatResult.VatTreatment == VatTreatments.EuBelowThreshold &&
            !string.IsNullOrWhiteSpace(org.BillingCountry) &&
            !string.Equals(org.BillingCountry, "IT", StringComparison.OrdinalIgnoreCase))
        {
            await ossRevenueTracker.RecordEuB2cCrossBorderRevenueAsync(amountExVat);
        }

        var platformInvoice = new PlatformInvoice
        {
            OrgId = org.Id,
            StripeInvoiceId = invoice.Id,
            AmountExVat = amountExVat,
            VatAmount = vatResult.VatAmount,
            TotalAmount = totalAmount,
            VatTreatment = vatResult.VatTreatment,
            OssApplied = vatResult.OssApplied,
            SdiStatus = "pending",
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.PlatformInvoices.Add(platformInvoice);
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);

        if (string.Equals(org.BillingCountry, "IT", StringComparison.OrdinalIgnoreCase))
        {
            var transmissionId = await sdiEInvoiceService.TransmitInvoiceAsync(platformInvoice);
            if (!string.IsNullOrWhiteSpace(transmissionId))
            {
                platformInvoice.SdiTransmissionId = transmissionId;
                platformInvoice.SdiStatus = "sent";
                await dbContext.SaveChangesAsync();
            }
        }
    }

    private async Task HandleInvoicePaymentFailedAsync(Invoice? invoice)
    {
        if (invoice is null)
            return;

        var org = await ResolveOrgForInvoiceAsync(invoice);
        if (org is null)
            return;

        org.SubscriptionStatus = SubscriptionStatus.PastDue;
        org.PastDueSince ??= DateTime.UtcNow;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);
    }

    private async Task<Org?> ResolveOrgForSubscriptionAsync(Subscription subscription)
    {
        if (subscription.Metadata.TryGetValue("orgId", out var orgIdRaw) &&
            Guid.TryParse(orgIdRaw, out var orgId))
        {
            var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org is not null)
                return org;
        }

        var customerId = subscription.CustomerId;
        if (!string.IsNullOrWhiteSpace(customerId))
            return await dbContext.Orgs.FirstOrDefaultAsync(o => o.StripeCustomerId == customerId);

        return null;
    }

    private async Task<Org?> ResolveOrgForInvoiceAsync(Invoice invoice)
    {
        if (invoice.Metadata.TryGetValue("orgId", out var orgIdRaw) &&
            Guid.TryParse(orgIdRaw, out var orgId))
        {
            var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org is not null)
                return org;
        }

        var customerId = invoice.CustomerId;
        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.StripeCustomerId == customerId);
            if (org is not null)
                return org;
        }

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            return await dbContext.Orgs.FirstOrDefaultAsync(o => o.SubscriptionId == subscriptionId);

        return null;
    }

    private static SubscriptionStatus MapSubscriptionStatus(string? stripeStatus, string eventType)
    {
        if (eventType == "customer.subscription.deleted")
            return SubscriptionStatus.Canceled;

        return stripeStatus switch
        {
            "trialing" => SubscriptionStatus.Trialing,
            "active" => SubscriptionStatus.Active,
            "past_due" => SubscriptionStatus.PastDue,
            "canceled" or "unpaid" => SubscriptionStatus.Canceled,
            _ => SubscriptionStatus.None,
        };
    }

    private static decimal ConvertCentsToDecimal(long? cents) =>
        cents.HasValue ? Math.Round(cents.Value / 100m, 2) : 0m;

    private async Task HandleAccountUpdatedAsync(Account? account)
    {
        if (account is null)
            return;

        logger.LogInformation("Connect account updated: {AccountId}", account.Id);
        var snapshot = StripeConnectGateway.MapAccount(account);
        await connectOnboardingService.ApplyAccountUpdatedAsync(snapshot);
    }

    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (TryGetMetadataKind(paymentIntent, out var kind))
        {
            if (string.Equals(kind, RentChargeKind, StringComparison.Ordinal))
            {
                if (source == WebhookSource.Connected &&
                    paymentIntent.Metadata.TryGetValue("rentLedgerEntryId", out var entryIdRaw) &&
                    Guid.TryParse(entryIdRaw, out var entryId))
                    await rentBillingService.HandleRentPaymentSucceededAsync(entryId);
                return;
            }
            if (string.Equals(kind, DirectBookingKind, StringComparison.Ordinal) && source == WebhookSource.Connected)
            {
                await HandleDirectBookingPaymentSucceededAsync(paymentIntent);
                return;
            }
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment succeeded: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Completed);
    }

    private async Task HandleDirectBookingPaymentSucceededAsync(PaymentIntent paymentIntent)
    {
        logger.LogInformation("Direct booking payment succeeded: {PaymentIntentId}", paymentIntent.Id);

        var payment = await paymentRepository.GetByTransactionIdAsync(paymentIntent.Id);
        if (payment is null)
        {
            logger.LogWarning("No payment row for direct booking PI {PaymentIntentId}", paymentIntent.Id);
            return;
        }

        if (payment.Status == PaymentStatus.Completed)
            return;

        payment.Status = PaymentStatus.Completed;
        payment.ProcessedAt = DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
        await paymentRepository.UpdateAsync(payment);

        var booking = await bookingRepository.GetByIdAsync(payment.BookingId);
        if (booking is null || booking.Status == BookingStatus.Confirmed)
            return;

        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;
        await bookingRepository.UpdateAsync(booking);
    }

    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent, WebhookSource source, string eventType)
    {
        if (paymentIntent is null)
            return;

        if (TryGetMetadataKind(paymentIntent, out var kind))
        {
            if (string.Equals(kind, RentChargeKind, StringComparison.Ordinal))
            {
                if (source == WebhookSource.Connected &&
                    paymentIntent.Metadata.TryGetValue("rentLedgerEntryId", out var entryIdRaw) &&
                    Guid.TryParse(entryIdRaw, out var entryId))
                    await rentBillingService.HandleRentPaymentFailedAsync(entryId, eventType == "payment_intent.canceled");
                return;
            }
            if (string.Equals(kind, DirectBookingKind, StringComparison.Ordinal) && source == WebhookSource.Connected)
            {
                logger.LogInformation("Direct booking payment failed/canceled: {PaymentIntentId}", paymentIntent.Id);
                await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
                return;
            }
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment failed: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
    }

    private async Task HandleRefundAsync(Charge? charge)
    {
        if (charge is null)
            return;

        logger.LogInformation("Charge refunded: {ChargeId}", charge.Id);
        var transactionId = charge.PaymentIntentId;
        if (string.IsNullOrEmpty(transactionId))
            return;

        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment is not null)
        {
            payment.Status = PaymentStatus.Refunded;
            await paymentRepository.UpdateAsync(payment);
        }
    }

    private static bool TryGetMetadataKind(PaymentIntent paymentIntent, out string? kind) =>
        paymentIntent.Metadata.TryGetValue("kind", out kind) && !string.IsNullOrWhiteSpace(kind);

    private async Task UpdatePaymentStatusAsync(string transactionId, PaymentStatus status)
    {
        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment is null)
            return;

        payment.Status = status;
        if (status == PaymentStatus.Completed)
            payment.ProcessedAt = DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
        await paymentRepository.UpdateAsync(payment);
    }
}
