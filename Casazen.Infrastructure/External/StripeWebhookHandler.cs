using Stripe;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

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
    IPaymentRefundService paymentRefundService,
    CheckoutPaymentSettlementService checkoutPayments,
    ILogger<StripeWebhookHandler> logger)
{
    private const string DirectBookingKind = "direct-booking";
    private const string DirectBookingDeadlineChargeKind = "direct-booking-deadline-charge";
    private const string DeadlineChargeDescription = "Direct checkout - deferred payment (charged at deadline)";
    private const string LegacyDeadlineChargeDescription = "Direct booking - charged at deadline";
    private const string RentChargeKind = "rent-charge";

    public Task HandleEventAsync(Event stripeEvent) =>
        HandleEventAsync(stripeEvent, WebhookSource.Platform);

    /// <summary>
    /// Applies one Stripe event exactly once, for the platform and the Connect endpoint alike (A1-09, A3-03).
    /// On PostgreSQL the claim of <see cref="Event.Id"/> (insert into <c>ProcessedStripeEvents</c>, primary key)
    /// and the business updates share one transaction:
    /// <list type="bullet">
    ///   <item>a duplicate delivered later finds the committed claim and is skipped;</item>
    ///   <item>a duplicate processed in parallel blocks on the uncommitted claim, then gets a unique violation
    ///   (skipped) when the first worker commits, or claims the event itself when the first worker rolls back;</item>
    ///   <item>a failure rolls back the claim with every partial update and is rethrown, so the Hangfire retry
    ///   or a new delivery from Stripe processes the event again.</item>
    /// </list>
    /// The signature was verified by <c>WebhooksController</c> before the event was queued.
    /// </summary>
    public async Task HandleEventAsync(Event stripeEvent, WebhookSource source)
    {
        await using var eventTransaction = await BeginEventTransactionAsync();

        if (!await TryClaimEventAsync(stripeEvent, source))
        {
            if (eventTransaction is not null)
                await eventTransaction.RollbackAsync();

            logger.LogInformation(
                "Skipping duplicate Stripe event {EventId} ({EventType}, source={Source})",
                stripeEvent.Id,
                stripeEvent.Type,
                source);
            return;
        }

        // Refunds that Stripe has just confirmed: the guest is emailed once the event is committed.
        IReadOnlyList<Guid> succeededRefunds = [];
        // Booking payment confirmed again or refunded (BK-04): Stripe call and email once the event is committed.
        CheckoutPaymentSettlement? checkoutSettlement = null;
        try
        {
            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    checkoutSettlement = await HandlePaymentSucceededAsync(
                        stripeEvent.Data.Object as PaymentIntent, source, stripeEvent.Account);
                    break;
                case "payment_intent.payment_failed":
                case "payment_intent.canceled":
                    await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent, source, stripeEvent.Type);
                    break;
                case "setup_intent.succeeded":
                    if (source == WebhookSource.Connected)
                        await HandleSetupIntentSucceededAsync(stripeEvent.Data.Object as SetupIntent);
                    break;
                case "charge.refunded":
                    succeededRefunds = await HandleChargeRefundedAsync(stripeEvent.Data.Object as Charge, source, stripeEvent.Account);
                    break;
                case "refund.created":
                case "refund.updated":
                case "refund.failed":
                case "charge.refund.updated":
                    succeededRefunds = await HandleRefundChangedAsync(stripeEvent.Data.Object as Refund, source, stripeEvent.Account);
                    break;
                case "account.updated":
                    if (source == WebhookSource.Connected)
                        await HandleAccountUpdatedAsync(stripeEvent.Data.Object as Account);
                    break;
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                    if (source == WebhookSource.Platform)
                        await HandleSubscriptionChangedAsync(stripeEvent.Data.Object as Subscription, stripeEvent.Type, stripeEvent.Id);
                    break;
                case "invoice.paid":
                    if (source == WebhookSource.Platform)
                        await HandleInvoicePaidAsync(stripeEvent.Data.Object as Invoice, stripeEvent.Id);
                    break;
                case "invoice.payment_failed":
                    if (source == WebhookSource.Platform)
                        await HandleInvoicePaymentFailedAsync(stripeEvent.Data.Object as Invoice, stripeEvent.Id);
                    break;
                default:
                    logger.LogInformation("Unhandled Stripe event: {EventType} (source={Source})", stripeEvent.Type, source);
                    break;
            }

            if (eventTransaction is not null)
                await eventTransaction.CommitAsync();
        }
        catch (Exception ex)
        {
            if (eventTransaction is not null)
            {
                await eventTransaction.RollbackAsync();
                // The tracked entities no longer match the database after the rollback.
                dbContext.ChangeTracker.Clear();
            }
            else
            {
                await RemoveClaimedEventAsync(stripeEvent.Id);
            }

            logger.LogError(
                ex,
                "Error handling Stripe event {EventId} ({EventType}, source={Source}): claim released for the retry",
                stripeEvent.Id,
                stripeEvent.Type,
                source);
            throw;
        }

        foreach (var refundId in succeededRefunds)
            await paymentRefundService.NotifyGuestAsync(refundId);

        if (checkoutSettlement is not null)
            await checkoutPayments.CompleteAsync(checkoutSettlement);
    }

    private async Task<IDbContextTransaction?> BeginEventTransactionAsync()
    {
        if (!dbContext.Database.IsRelational())
            return null;

        return await dbContext.Database.BeginTransactionAsync();
    }

    // Returns false if the event was already processed/claimed by another worker; true if claimed.
    // Production relational providers keep this insert uncommitted until business logic succeeds.
    // Events with no Id (e.g. synthetic test events) are always processed and not tracked.
    private async Task<bool> TryClaimEventAsync(Event stripeEvent, WebhookSource source)
    {
        if (string.IsNullOrEmpty(stripeEvent.Id))
            return true;

        if (await dbContext.ProcessedStripeEvents
                .AsNoTracking()
                .AnyAsync(e => e.EventId == stripeEvent.Id))
            return false;

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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another worker committed the same event while this insert waited on its uncommitted claim.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task RemoveClaimedEventAsync(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId))
            return;

        var processed = await dbContext.ProcessedStripeEvents.FindAsync(eventId);
        if (processed is null)
            return;

        dbContext.ProcessedStripeEvents.Remove(processed);
        await dbContext.SaveChangesAsync();
    }

    private async Task HandleSubscriptionChangedAsync(Subscription? subscription, string eventType, string eventId)
    {
        if (subscription is null)
            return;

        var org = await ResolveOrgForSubscriptionAsync(subscription);
        if (org is null)
        {
            logger.LogError("No org resolved for subscription {SubscriptionId} (event {EventId})", subscription.Id, eventId);
            return;
        }

        var status = eventType == "customer.subscription.deleted"
            ? SubscriptionStatus.Canceled
            : BillingSubscriptionPolicy.MapStripeStatus(subscription.Status);

        if (IsOutdatedSubscriptionEvent(org, subscription.Id, status, eventId))
            return;

        if (!string.Equals(org.SubscriptionId, subscription.Id, StringComparison.Ordinal))
            org.PastDueSince = null; // the grace period belongs to the previous subscription

        org.SubscriptionId = subscription.Id;
        org.SubscriptionStatus = status;
        org.CurrentPeriodEnd = subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd;

        if (status is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
            org.PastDueSince = null;
        else if (status == SubscriptionStatus.PastDue && org.PastDueSince is null)
            org.PastDueSince = DateTime.UtcNow;

        // A1-11: the tier of the price is granted only once Stripe reports the subscription active or trialing. An
        // incomplete (first payment not succeeded), unpaid or paused subscription never sets the tier it has not
        // paid for; past due keeps the tier already paid (grace period, see EntitlementService).
        if (status is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
        {
            var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
            var tier = stripeBillingService.MapPriceIdToTier(priceId);
            if (tier.HasValue)
            {
                org.PlanTier = tier.Value;
            }
            else
            {
                logger.LogWarning(
                    "Subscription {SubscriptionId} (event {EventId}) has price {PriceId} not mapped to a plan tier: tier unchanged",
                    subscription.Id,
                    eventId,
                    priceId);
            }
        }

        if (!string.IsNullOrWhiteSpace(subscription.CustomerId))
            org.StripeCustomerId ??= subscription.CustomerId;

        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);
    }

    /// <summary>
    /// Stripe does not guarantee the order of events and Hangfire runs jobs in parallel, so an older event can be
    /// processed after a newer one. With fail-closed statuses that would lock out a paying org (a late
    /// <c>customer.subscription.created</c> with <c>incomplete</c> after the <c>active</c> update), or let an event of
    /// another subscription of the customer overwrite the one that is paying. Such events are ignored.
    /// </summary>
    private bool IsOutdatedSubscriptionEvent(Org org, string subscriptionId, SubscriptionStatus incoming, string eventId)
    {
        var stored = org.SubscriptionStatus;
        string? reason = null;

        if (string.Equals(org.SubscriptionId, subscriptionId, StringComparison.Ordinal))
        {
            // Stripe never moves a subscription back to incomplete; canceled and incomplete_expired are terminal.
            if (incoming == SubscriptionStatus.Incomplete && stored is not (SubscriptionStatus.None or SubscriptionStatus.Incomplete))
                reason = "a subscription never returns to incomplete";
            else if (stored == SubscriptionStatus.Canceled && incoming != SubscriptionStatus.Canceled)
                reason = "the subscription is already canceled";
        }
        else if (!string.IsNullOrWhiteSpace(org.SubscriptionId) && IsPaidSubscription(stored))
        {
            if (IsExistingSubscription(incoming))
            {
                logger.LogError(
                    "Org {OrgId} has a second subscription {SubscriptionId} ({SubscriptionStatus}, event {EventId}) besides {CurrentSubscriptionId}: the current one keeps the plan, cancel and refund the duplicate on Stripe",
                    org.Id,
                    subscriptionId,
                    incoming,
                    eventId,
                    org.SubscriptionId);
            }

            reason = "another subscription of the org drives the plan";
        }

        if (reason is null)
            return false;

        logger.LogInformation(
            "Ignoring Stripe event {EventId} for subscription {SubscriptionId} ({SubscriptionStatus}): {Reason}",
            eventId,
            subscriptionId,
            incoming,
            reason);
        return true;
    }

    /// <summary>A subscription that has been paid and still exists on Stripe.</summary>
    private static bool IsPaidSubscription(SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue or SubscriptionStatus.Unpaid;

    private static bool IsExistingSubscription(SubscriptionStatus status) =>
        IsPaidSubscription(status) || status == SubscriptionStatus.Incomplete;

    private async Task HandleInvoicePaidAsync(Invoice? invoice, string eventId)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Id))
            return;

        if (await dbContext.PlatformInvoices.AnyAsync(i => i.StripeInvoiceId == invoice.Id))
            return;

        var org = await ResolveOrgForInvoiceAsync(invoice);
        if (org is null)
        {
            logger.LogError("No org resolved for invoice {InvoiceId} (event {EventId})", invoice.Id, eventId);
            return;
        }

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            MarkSubscriptionPaid(org, subscriptionId, invoice.Id, eventId);

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

    /// <summary>
    /// A paid invoice of the org's subscription makes it active again (past due or unpaid → active). It never
    /// reactivates a canceled subscription or changes the status on behalf of another subscription of the customer
    /// (events out of order, duplicate). The tier is set by the <c>customer.subscription.*</c> events.
    /// </summary>
    private void MarkSubscriptionPaid(Org org, string subscriptionId, string invoiceId, string eventId)
    {
        var sameSubscription = string.Equals(org.SubscriptionId, subscriptionId, StringComparison.Ordinal);
        if (!sameSubscription && !string.IsNullOrWhiteSpace(org.SubscriptionId))
        {
            logger.LogWarning(
                "Invoice {InvoiceId} (event {EventId}) paid for subscription {SubscriptionId}, not the org's current {CurrentSubscriptionId}: status unchanged",
                invoiceId,
                eventId,
                subscriptionId,
                org.SubscriptionId);
            return;
        }

        if (sameSubscription && org.SubscriptionStatus == SubscriptionStatus.Canceled)
        {
            logger.LogInformation(
                "Invoice {InvoiceId} (event {EventId}) paid for canceled subscription {SubscriptionId}: status unchanged",
                invoiceId,
                eventId,
                subscriptionId);
            return;
        }

        org.SubscriptionId = subscriptionId;
        org.SubscriptionStatus = SubscriptionStatus.Active;
        org.PastDueSince = null;
        org.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A failed renewal of the org's paying subscription starts the past-due grace period. A failed first payment
    /// (<c>billing_reason = subscription_create</c>) is not a renewal: the subscription stays incomplete, without
    /// paid access (A1-11). Invoices of another subscription, or of a canceled, incomplete or unpaid one, change
    /// nothing.
    /// </summary>
    private async Task HandleInvoicePaymentFailedAsync(Invoice? invoice, string eventId)
    {
        if (invoice is null)
            return;

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            logger.LogInformation("Failed invoice {InvoiceId} (event {EventId}) has no subscription: ignored", invoice.Id, eventId);
            return;
        }

        if (string.Equals(invoice.BillingReason, "subscription_create", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "First payment of subscription {SubscriptionId} failed (invoice {InvoiceId}, event {EventId}): it stays incomplete",
                subscriptionId,
                invoice.Id,
                eventId);
            return;
        }

        var org = await ResolveOrgForInvoiceAsync(invoice);
        if (org is null)
        {
            logger.LogError("No org resolved for failed invoice {InvoiceId} (event {EventId})", invoice.Id, eventId);
            return;
        }

        if (!string.Equals(org.SubscriptionId, subscriptionId, StringComparison.Ordinal) ||
            org.SubscriptionStatus is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue))
        {
            logger.LogInformation(
                "Failed invoice {InvoiceId} (event {EventId}) of subscription {SubscriptionId}: the org's subscription {CurrentSubscriptionId} is {SubscriptionStatus}, status unchanged",
                invoice.Id,
                eventId,
                subscriptionId,
                org.SubscriptionId,
                org.SubscriptionStatus);
            return;
        }

        org.SubscriptionStatus = SubscriptionStatus.PastDue;
        org.PastDueSince ??= DateTime.UtcNow;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);
    }

    private async Task<Org?> ResolveOrgForSubscriptionAsync(Subscription subscription)
    {
        if (subscription.Metadata?.TryGetValue("orgId", out var orgIdRaw) == true &&
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
        if (invoice.Metadata?.TryGetValue("orgId", out var orgIdRaw) == true &&
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

    /// <summary>
    /// A PaymentIntent succeeded. Rent charges and deferred deadline charges have their own handlers; the checkout's
    /// PaymentIntents (<c>direct-booking</c>) on the Connect endpoint, and every booking payment reported by the platform
    /// endpoint, are settled by <see cref="CheckoutPaymentSettlementService"/>: the booking is confirmed, confirmed again
    /// when the dates are still free, or refunded in full when they are not (BK-04, A3-04). Never "Completed" on a
    /// cancelled booking without one of the two.
    /// </summary>
    private async Task<CheckoutPaymentSettlement?> HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source, string? account)
    {
        if (paymentIntent is null)
            return null;

        TryGetMetadataKind(paymentIntent, out var kind);
        if (string.Equals(kind, RentChargeKind, StringComparison.Ordinal))
        {
            if (source == WebhookSource.Connected &&
                paymentIntent.Metadata.TryGetValue("rentLedgerEntryId", out var entryIdRaw) &&
                Guid.TryParse(entryIdRaw, out var entryId))
                await rentBillingService.HandleRentPaymentSucceededAsync(entryId);
            return null;
        }
        if (string.Equals(kind, DirectBookingDeadlineChargeKind, StringComparison.Ordinal) && source == WebhookSource.Connected)
        {
            await HandleDirectBookingDeadlineChargeSucceededAsync(paymentIntent, account);
            return null;
        }

        // On the Connect endpoint only the checkout's own PaymentIntents: the host's other charges are not CasaZen's.
        if (source == WebhookSource.Connected && !string.Equals(kind, DirectBookingKind, StringComparison.Ordinal))
            return null;

        logger.LogInformation(
            "Booking payment succeeded: {PaymentIntentId} (source={Source}, account={AccountId})",
            paymentIntent.Id,
            source,
            account ?? "none");
        return await checkoutPayments.SettleSucceededPaymentAsync(paymentIntent.Id, source, account);
    }

    private async Task HandleSetupIntentSucceededAsync(SetupIntent? setupIntent)
    {
        if (setupIntent is null)
            return;

        logger.LogInformation("Direct booking setup intent succeeded: {SetupIntentId}", setupIntent.Id);

        if (!TryGetMetadataKind(setupIntent, out var kind) || !string.Equals(kind, "direct-booking-setup", StringComparison.Ordinal))
            return;

        if (!setupIntent.Metadata.TryGetValue("bookingId", out var bookingIdRaw) || !Guid.TryParse(bookingIdRaw, out var bookingId))
        {
            logger.LogWarning("Setup intent has no bookingId metadata: {SetupIntentId}", setupIntent.Id);
            return;
        }

        var booking = await bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            logger.LogWarning("No booking found for setup intent: {BookingId}", bookingId);
            return;
        }

        if (booking.Status != BookingStatus.Pending)
        {
            logger.LogWarning(
                "Ignoring setup intent {SetupIntentId} for booking {BookingId} in status {Status}",
                setupIntent.Id,
                bookingId,
                booking.Status);
            return;
        }

        var paymentMethodId = setupIntent.PaymentMethodId;
        if (string.IsNullOrWhiteSpace(paymentMethodId))
        {
            logger.LogWarning("Setup intent has no payment method: {SetupIntentId}", setupIntent.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(setupIntent.CustomerId))
        {
            logger.LogWarning("Setup intent has no customer: {SetupIntentId}", setupIntent.Id);
            return;
        }

        booking.StripePaymentMethodId = paymentMethodId;
        booking.StripeCustomerId = setupIntent.CustomerId;
        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;
        await bookingRepository.UpdateAsync(booking);

        logger.LogInformation("Booking {BookingId} confirmed with payment method {PaymentMethodId}", bookingId, paymentMethodId);
    }

    private async Task HandleDirectBookingDeadlineChargeSucceededAsync(PaymentIntent paymentIntent, string? account)
    {
        logger.LogInformation("Direct booking deadline charge succeeded: {PaymentIntentId}", paymentIntent.Id);

        var existingPaymentIntent = await paymentRepository.GetByTransactionIdAsync(paymentIntent.Id);
        if (existingPaymentIntent is not null)
        {
            if (existingPaymentIntent.Status != PaymentStatus.Completed)
            {
                existingPaymentIntent.Status = PaymentStatus.Completed;
                existingPaymentIntent.StripePaymentIntentId = paymentIntent.Id;
                existingPaymentIntent.StripeAccountId ??= account;
                existingPaymentIntent.ProcessedAt = DateTime.UtcNow;
                existingPaymentIntent.UpdatedAt = DateTime.UtcNow;
                await paymentRepository.UpdateAsync(existingPaymentIntent);
            }
            return;
        }

        if (!paymentIntent.Metadata.TryGetValue("bookingId", out var bookingIdRaw) ||
            !Guid.TryParse(bookingIdRaw, out var bookingId))
        {
            logger.LogWarning("Deadline charge payment intent has no bookingId metadata: {PaymentIntentId}", paymentIntent.Id);
            return;
        }

        var booking = await bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            logger.LogWarning("No booking found for deadline charge payment intent: {BookingId}", bookingId);
            return;
        }

        var payments = (await paymentRepository.GetByBookingAsync(booking.Id)).ToList();
        var deferredPayment = payments.FirstOrDefault(p =>
            p.Status == PaymentStatus.Pending &&
            (p.TransactionId == booking.StripeSetupIntentId ||
             p.Description == DeadlineChargeDescription ||
             p.Description == LegacyDeadlineChargeDescription));

        if (deferredPayment is not null)
        {
            deferredPayment.Status = PaymentStatus.Completed;
            deferredPayment.TransactionId = paymentIntent.Id;
            deferredPayment.StripePaymentIntentId = paymentIntent.Id;
            deferredPayment.StripeAccountId ??= account;
            deferredPayment.ProcessedAt = DateTime.UtcNow;
            deferredPayment.UpdatedAt = DateTime.UtcNow;
            await paymentRepository.UpdateAsync(deferredPayment);
            return;
        }

        await paymentRepository.AddAsync(new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Completed,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = paymentIntent.Id,
            StripePaymentIntentId = paymentIntent.Id,
            StripeAccountId = account,
            Description = DeadlineChargeDescription,
            ProcessedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
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
                await UpdatePaymentStatusAsync(paymentIntent.Id, FailedOrCanceled(eventType));
                return;
            }
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment failed: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, FailedOrCanceled(eventType));
    }

    // A PaymentIntent canceled with its booking (BK-02) was never collected: Canceled, not Failed.
    private static PaymentStatus FailedOrCanceled(string eventType) =>
        eventType == "payment_intent.canceled" ? PaymentStatus.Canceled : PaymentStatus.Failed;

    /// <summary>
    /// A charge was refunded, fully or partly, from CasaZen or from the Stripe Dashboard (A3-05). Since API version
    /// 2022-11-15 the charge does not embed its refunds, so they are read from Stripe (on the event's connected
    /// account) and applied one by one: the payment is Refunded / PartiallyRefunded only for refunds that succeeded.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> HandleChargeRefundedAsync(Charge? charge, WebhookSource source, string? account)
    {
        if (charge is null || string.IsNullOrWhiteSpace(charge.PaymentIntentId))
            return [];

        logger.LogInformation("Charge refunded: {ChargeId} ({PaymentIntentId}, source={Source})", charge.Id, charge.PaymentIntentId, source);
        if (!TryGetRefundAccount(source, account, out var connectedAccountId))
            return [];

        if (charge.Refunds?.Data is { Count: > 0 } embedded)
        {
            return await paymentRefundService.ApplyStripeRefundsAsync(
                embedded.Select(PaymentRefundService.ToSnapshot).ToList(),
                connectedAccountId);
        }

        return await paymentRefundService.SyncPaymentIntentRefundsAsync(charge.PaymentIntentId, connectedAccountId);
    }

    /// <summary>A refund was created or changed status (pending → succeeded, failed, canceled).</summary>
    private async Task<IReadOnlyList<Guid>> HandleRefundChangedAsync(Refund? refund, WebhookSource source, string? account)
    {
        if (refund is null || string.IsNullOrWhiteSpace(refund.PaymentIntentId))
            return [];

        logger.LogInformation("Refund {RefundId} is {Status} (source={Source})", refund.Id, refund.Status, source);
        if (!TryGetRefundAccount(source, account, out var connectedAccountId))
            return [];

        return await paymentRefundService.ApplyStripeRefundsAsync([PaymentRefundService.ToSnapshot(refund)], connectedAccountId);
    }

    /// <summary>Connect events carry the connected account they happened on; platform events have none.</summary>
    private bool TryGetRefundAccount(WebhookSource source, string? account, out string? connectedAccountId)
    {
        connectedAccountId = null;
        if (source == WebhookSource.Platform)
            return true;

        if (string.IsNullOrWhiteSpace(account))
        {
            logger.LogWarning("Connect refund event without account: ignored");
            return false;
        }

        connectedAccountId = account;
        return true;
    }

    private static bool TryGetMetadataKind(PaymentIntent paymentIntent, out string? kind) =>
        paymentIntent.Metadata.TryGetValue("kind", out kind) && !string.IsNullOrWhiteSpace(kind);

    private static bool TryGetMetadataKind(SetupIntent setupIntent, out string? kind) =>
        setupIntent.Metadata.TryGetValue("kind", out kind) && !string.IsNullOrWhiteSpace(kind);

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
