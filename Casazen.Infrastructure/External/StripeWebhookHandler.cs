using Stripe;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.External;

public class StripeWebhookHandler(
    IPaymentRepository paymentRepository,
    IBookingRepository bookingRepository,
    IConnectOnboardingService connectOnboardingService,
    AppDbContext dbContext,
    IStripeBillingService stripeBillingService,
    IEntitlementService entitlementService,
    IPlatformInvoiceService platformInvoices,
    IRentBillingService rentBillingService,
    IPaymentRefundService paymentRefundService,
    CheckoutPaymentSettlementService checkoutPayments,
    DeferredChargeService deferredCharges,
    ISupplierPaymentWebhookService supplierPayments,
    ILogger<StripeWebhookHandler> logger,
    IActivityLog? activityLog = null,
    ConcordatoFacilePaymentHandler? concordatoFacilePayments = null,
    IConcordatoFacileGiftService? concordatoFacileGiftService = null,
    IOptions<ConcordatoFacileOptions>? concordatoFacileOptions = null,
    ISupplierShowcaseContentService? showcaseContent = null,
    IShowcaseBookingService? showcaseBookings = null)
{
    private const string DirectBookingKind = "direct-booking";

    // ConcordatoFacile one-time payment (ADR-005). Optional like the activity log, so that the existing tests keep building the
    // handler as they always did; the container always provides it.
    private ConcordatoFacilePaymentHandler CfPayments =>
        concordatoFacilePayments ??= new ConcordatoFacilePaymentHandler(dbContext, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

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

        // A ConcordatoFacile event (ADR-005) is claimed under its own source (R5). Decided once, here: the dispatch below reuses it.
        var cfPayment = stripeEvent.Type == "payment_intent.succeeded"
            && IsConcordatoFacilePayment(stripeEvent.Data.Object as PaymentIntent);
        var cfRefund = stripeEvent.Type == "charge.refunded"
            && await IsConcordatoFacileRefundAsync(stripeEvent.Data.Object as Charge);
        // CF-BE: a payment of the service that failed or whose PaymentIntent was canceled. It closes the purchase only when the Checkout
        // Session cannot succeed any more (ConcordatoFacilePaymentHandler.HandlePaymentNotCompletedAsync).
        var cfPaymentEnded = stripeEvent.Type is "payment_intent.payment_failed" or "payment_intent.canceled"
            && IsConcordatoFacilePayment(stripeEvent.Data.Object as PaymentIntent);

        if (!await TryClaimEventAsync(stripeEvent, cfPayment || cfRefund || cfPaymentEnded ? WebhookSource.ConcordatoFacile : source))
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
        // Booking confirmed (BK-10), confirmed again or refunded (BK-04): Stripe call and emails once the event is committed.
        CheckoutPaymentSettlement? checkoutSettlement = null;
        // Deferred charge failed (BK-08): guest and host emails once the event is committed.
        DeferredChargeNotice? deferredChargeNotice = null;
        // Rent installment paid or failed (LT-06): landlord or tenant emails once the event is committed.
        RentPaymentNotice? rentNotice = null;
        // Platform invoice paid (PL-13): submitted to the configured SDI provider once the event is committed.
        Guid? sdiSubmission = null;
        // Payment of a supplier's service (SP-15b): the receipt, the new link, the alert to the admins, the refund notices, once the event is committed.
        IReadOnlyList<ServicePaymentNotice> servicePaymentNotices = [];
        // The card of a booking from a supplier's showcase was blocked and completed the booking (SP-15c-a): the receipt to the customer and the
        // news to the supplier go once the event is committed.
        ShowcaseHoldCompletion? holdCompletion = null;
        // A supplier that has just become able to receive payments (SP-15b): the payment requests that waited for it go out once the event is committed.
        Guid? readySupplierOrgId = null;
        try
        {
            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded" when IsDeferredCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.processing" when IsDeferredCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.payment_failed" when IsDeferredCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.canceled" when IsDeferredCharge(stripeEvent.Data.Object as PaymentIntent):
                    (checkoutSettlement, deferredChargeNotice) = await HandleDeferredChargeAsync(
                        (PaymentIntent)stripeEvent.Data.Object, stripeEvent.Type, source, stripeEvent.Account);
                    break;
                case "payment_intent.succeeded" when IsRentCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.processing" when IsRentCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.payment_failed" when IsRentCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.canceled" when IsRentCharge(stripeEvent.Data.Object as PaymentIntent):
                    rentNotice = await HandleRentChargeAsync(
                        (PaymentIntent)stripeEvent.Data.Object, stripeEvent.Type, source, stripeEvent.Account);
                    break;
                // ConcordatoFacile one-time payment (ADR-005): BEFORE the generic payment_intent.succeeded, like rent and service charges.
                // CF payments are identified by leaseId in metadata without a "kind" field.
                case "payment_intent.succeeded" when cfPayment:
                    await CfPayments.HandlePaymentIntentSucceededAsync((PaymentIntent)stripeEvent.Data.Object, stripeEvent.Created);
                    break;
                // The same payments when they fail or are canceled (CF-BE): BEFORE the generic failure below, which knows nothing of them.
                case "payment_intent.payment_failed" when cfPaymentEnded:
                case "payment_intent.canceled" when cfPaymentEnded:
                    await CfPayments.HandlePaymentNotCompletedAsync((PaymentIntent)stripeEvent.Data.Object, stripeEvent.Type);
                    break;
                // The payment of a supplier's service (SP-15b, kind = service-charge): BEFORE the generic payment_intent.succeeded
                // below, like the rent. Otherwise the event would be "processed" with no effect on the Connect endpoint (the generic
                // handler drops every kind but direct-booking) or, on the platform endpoint, handed to the booking settlement.
                case "payment_intent.succeeded" when IsServiceCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.processing" when IsServiceCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.payment_failed" when IsServiceCharge(stripeEvent.Data.Object as PaymentIntent):
                case "payment_intent.canceled" when IsServiceCharge(stripeEvent.Data.Object as PaymentIntent):
                // SP-15c-a: the card of a booking is blocked (the PaymentIntent of the money CasaZen holds is a manual-capture one).
                case "payment_intent.amount_capturable_updated" when IsServiceCharge(stripeEvent.Data.Object as PaymentIntent):
                    (servicePaymentNotices, holdCompletion) = await HandleServiceChargeAsync(
                        stripeEvent, (PaymentIntent)stripeEvent.Data.Object, source);
                    break;
                // SP-15c-a: the charge side of the same events. The PaymentIntent events carry the same facts; these are handled as the
                // PaymentIntent event they accompany, so a missed one is made good by the other (idempotent).
                case "charge.captured" when IsServiceChargeOf(stripeEvent.Data.Object as Charge):
                case "charge.expired" when IsServiceChargeOf(stripeEvent.Data.Object as Charge):
                    servicePaymentNotices = await HandleServiceChargeEventAsync(stripeEvent, (Charge)stripeEvent.Data.Object);
                    break;
                // SP-15c-a: the transfer that pays a supplier, and its reversal.
                case "transfer.created":
                case "transfer.reversed":
                    servicePaymentNotices = await HandleServiceTransferAsync(stripeEvent);
                    break;
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
                        checkoutSettlement = await HandleSetupIntentSucceededAsync(stripeEvent.Data.Object as SetupIntent);
                    break;
                case "charge.refunded":
                    {
                        // A refund of a supplier's service payment is recorded by the service payments (SP-15b); a CF refund is
                        // handled by the CF handler (ADR-005-R7); any other is a booking payment's, as before.
                        var charge = stripeEvent.Data.Object as Charge;
                        var serviceRefund = await HandleServiceChargeRefundedAsync(charge, stripeEvent.Account);
                        if (serviceRefund is { Handled: true })
                            servicePaymentNotices = serviceRefund.Notices;
                        else if (cfRefund)
                            await CfPayments.HandleRefundAsync(charge!);
                        else
                            succeededRefunds = await HandleChargeRefundedAsync(charge, source, stripeEvent.Account);
                        break;
                    }
                case "refund.created":
                case "refund.updated":
                case "refund.failed":
                case "charge.refund.updated":
                    {
                        var serviceRefund = await HandleServiceRefundChangedAsync(stripeEvent.Data.Object as Refund, stripeEvent.Account);
                        if (serviceRefund is { Handled: true })
                            servicePaymentNotices = serviceRefund.Notices;
                        else
                            succeededRefunds = await HandleRefundChangedAsync(stripeEvent.Data.Object as Refund, source, stripeEvent.Account);
                        break;
                    }
                case "charge.dispute.created":
                case "charge.dispute.updated":
                case "charge.dispute.closed":
                case "charge.dispute.funds_withdrawn":
                case "charge.dispute.funds_reinstated":
                    {
                        // A dispute on a supplier's service payment: error in the log and an email to the admins (SP-15b); since SP-15c-a
                        // it is also recorded on the payment, and the platform endpoint reports the ones on the money CasaZen holds.
                        // Disputes of other payments are not handled here, as before.
                        var dispute = await HandleServiceDisputeAsync(stripeEvent);
                        if (dispute is { Handled: true })
                            servicePaymentNotices = dispute.Notices;
                        else
                            logger.LogInformation("Unhandled Stripe event: {EventType} (source={Source})", stripeEvent.Type, source);
                        break;
                    }
                case "account.updated":
                    if (source == WebhookSource.Connected)
                        readySupplierOrgId = await HandleAccountUpdatedAsync(stripeEvent.Data.Object as Account);
                    break;
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                    if (source == WebhookSource.Platform)
                        await HandleSubscriptionChangedAsync(stripeEvent.Data.Object as Subscription, stripeEvent.Type, stripeEvent.Id);
                    break;
                case "invoice.paid":
                    if (source == WebhookSource.Platform)
                        sdiSubmission = await HandleInvoicePaidAsync(stripeEvent.Data.Object as Invoice, stripeEvent.Id);
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

        if (deferredChargeNotice is not null)
            await deferredCharges.CompleteAsync(deferredChargeNotice);

        if (rentNotice is not null)
            await rentBillingService.CompleteAsync(rentNotice);

        if (sdiSubmission is { } platformInvoiceId)
            await platformInvoices.SubmitToSdiAsync(platformInvoiceId);

        if (servicePaymentNotices.Count > 0)
            await supplierPayments.CompleteAsync(servicePaymentNotices);

        if (holdCompletion is not null && showcaseBookings is not null)
            await showcaseBookings.CompleteHoldAuthorizationAsync(holdCompletion);

        if (readySupplierOrgId is { } supplierOrgId)
            supplierPayments.ScheduleSendPendingRequests(supplierOrgId);
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
                // The activity log (AM-02b) hears of the plan change in the very save that writes it: nobody asked, the
                // subscription did, so there is no actor.
                if (org.PlanTier != tier.Value)
                {
                    activityLog?.Record(OrgActivity.Of(
                        org.Id,
                        OrgActivityType.PlanChanged,
                        actorUserId: null,
                        org.Id.ToString(),
                        (OrgActivityDetailKeys.FromTier, org.PlanTier.ToString()),
                        (OrgActivityDetailKeys.ToTier, tier.Value.ToString()),
                        (OrgActivityDetailKeys.Source, PlanChangeSource.Subscription.Code())));
                }

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

        // ADR-006-R1, ADR-006-R2: Grant ConcordatoFacile gift for biennial subscriptions
        // Only on customer.subscription.created (not updated/deleted)
        if (eventType == "customer.subscription.created" && concordatoFacileGiftService is not null)
        {
            var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
            if (!string.IsNullOrWhiteSpace(priceId) && IsBiennialSubscriptionPrice(priceId))
            {
                logger.LogInformation(
                    "Biennial subscription {SubscriptionId} detected for org {OrgId} with price {PriceId}; granting ConcordatoFacile gift",
                    subscription.Id,
                    org.Id,
                    priceId);
                await concordatoFacileGiftService.GrantGiftAsync(org.Id, subscription.Id);
            }
        }
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

    /// <summary>
    /// Records a paid platform invoice with the tax Stripe Tax computed on it (PL-13, A1-08) and reactivates the org's
    /// subscription. Returns the id of the invoice to submit to the SDI provider after commit, if one is configured.
    /// </summary>
    private async Task<Guid?> HandleInvoicePaidAsync(Invoice? invoice, string eventId)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Id))
            return null;

        if (await dbContext.PlatformInvoices.AnyAsync(i => i.StripeInvoiceId == invoice.Id))
            return null;

        var org = await ResolveOrgForInvoiceAsync(invoice);
        if (org is null)
        {
            logger.LogError("No org resolved for invoice {InvoiceId} (event {EventId})", invoice.Id, eventId);
            return null;
        }

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            MarkSubscriptionPaid(org, subscriptionId, invoice.Id, eventId);

        var platformInvoice = await platformInvoices.RecordPaidInvoiceAsync(invoice, org);
        await dbContext.SaveChangesAsync();
        await entitlementService.SyncFromSubscriptionAsync(org.Id);

        return platformInvoice.SdiStatus == PlatformInvoiceSdiStatuses.Pending ? platformInvoice.Id : null;
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

    /// <summary>
    /// Applies <c>account.updated</c> to the org that owns the account. Returns the supplier org that has just become able to take
    /// charges and payouts, if it did (SP-15b): its pending payment requests are queued once the event is committed.
    /// </summary>
    private async Task<Guid?> HandleAccountUpdatedAsync(Account? account)
    {
        if (account is null)
            return null;

        logger.LogInformation("Connect account updated: {AccountId}", account.Id);
        var snapshot = StripeConnectGateway.MapAccount(account);
        var update = await connectOnboardingService.ApplyAccountUpdatedAsync(snapshot);

        // SP-13a: the bookings of the supplier's showcase follow its account. They close when it stops being able to take charges and
        // payouts (the new bookings; those already made stay) and open again when it can, inside the transaction of the event.
        if (showcaseContent is not null && update is { SupplierReadinessChanged: true, OrgId: { } supplierOrgId })
            await showcaseContent.ReconcileBookingsAsync(supplierOrgId);

        return update is { SupplierBecameReady: true, OrgId: { } orgId } ? orgId : null;
    }

    // ─── Supplier service payments (SP-15b) ─────────────────────────────────────────────────────────────────────────

    private static bool IsServiceCharge(PaymentIntent? paymentIntent) =>
        paymentIntent is not null &&
        TryGetMetadataKind(paymentIntent, out var kind) &&
        string.Equals(kind, ServiceCharges.Kind, StringComparison.Ordinal);

    /// <summary>
    /// An event of a service payment's PaymentIntent, from the Connect endpoint or from the platform endpoint listening to connected
    /// accounts (the event's <c>account</c>), applied in the event transaction and never dropped as processed without effect. A
    /// payment is recorded as paid only if account, amount, currency and commission are the ones that were asked for; otherwise it
    /// goes to review (<see cref="ISupplierPaymentWebhookService"/>).
    /// </summary>
    private async Task<(IReadOnlyList<ServicePaymentNotice> Notices, ShowcaseHoldCompletion? HoldCompletion)> HandleServiceChargeAsync(
        Event stripeEvent,
        PaymentIntent paymentIntent,
        WebhookSource source)
    {
        logger.LogInformation(
            "Service payment intent {PaymentIntentId}: {EventType} (source={Source}, account={AccountId})",
            paymentIntent.Id,
            stripeEvent.Type,
            source,
            stripeEvent.Account ?? "none");

        var paymentId = paymentIntent.Metadata is not null
                        && paymentIntent.Metadata.TryGetValue(ServiceCharges.PaymentMetadataKey, out var raw)
                        && Guid.TryParse(raw, out var parsed)
            ? parsed
            : (Guid?)null;

        // The booking hold a PaymentIntent of the money CasaZen holds blocks the card for, before any payment exists (SP-15c-a).
        var holdId = paymentIntent.Metadata is not null
                     && paymentIntent.Metadata.TryGetValue(ServiceCharges.HoldMetadataKey, out var holdRaw)
                     && Guid.TryParse(holdRaw, out var parsedHold)
            ? parsedHold
            : (Guid?)null;

        // The card of a booking is blocked and the booking has no request yet (SP-15c-a): the block is the second of the two conditions that
        // make the request (the other is the check of the e-mail). A hold that is already a request, or that does not exist, is not handled
        // here: the event is then about a payment, which follows below.
        if (stripeEvent.Type == "payment_intent.amount_capturable_updated"
            && holdId is { } hold
            && showcaseBookings is not null
            && paymentIntent.Metadata is not null
            && paymentIntent.Metadata.TryGetValue(ServiceCharges.SupplierMetadataKey, out var supplierRaw)
            && Guid.TryParse(supplierRaw, out var supplierOrgId))
        {
            var applied = await showcaseBookings.ApplyHoldAuthorizationAsync(new ShowcaseHoldAuthorization(
                supplierOrgId,
                hold,
                new HeldChargeObservation(
                    paymentIntent.Id,
                    stripeEvent.Account,
                    paymentIntent.Amount,
                    paymentIntent.AmountCapturable,
                    paymentIntent.AmountReceived,
                    paymentIntent.Currency,
                    paymentIntent.ApplicationFeeAmount),
                EventTime(stripeEvent)));
            if (applied.Handled)
                return (Array.Empty<ServicePaymentNotice>(), applied.Completion);
        }

        var notices = await supplierPayments.ApplyPaymentIntentEventAsync(new ServicePaymentIntentEvent(
            stripeEvent.Id ?? string.Empty,
            stripeEvent.Type,
            paymentIntent.Id,
            stripeEvent.Account,
            paymentId,
            paymentIntent.Amount,
            paymentIntent.AmountReceived,
            paymentIntent.Currency,
            paymentIntent.ApplicationFeeAmount,
            paymentIntent.LastPaymentError?.Code,
            EventTime(stripeEvent),
            paymentIntent.AmountCapturable,
            paymentIntent.LatestChargeId,
            holdId));
        return (notices, null);
    }

    /// <summary>A charge that carries the markers of a service PaymentIntent (Stripe copies the metadata of a PaymentIntent to its charge).</summary>
    private static bool IsServiceChargeOf(Charge? charge) =>
        charge is { Metadata: not null }
        && !string.IsNullOrWhiteSpace(charge.PaymentIntentId)
        && charge.Metadata.TryGetValue("kind", out var kind)
        && string.Equals(kind, ServiceCharges.Kind, StringComparison.Ordinal);

    /// <summary>
    /// <c>charge.captured</c> and <c>charge.expired</c> of the money CasaZen holds (SP-15c-a), applied as the PaymentIntent event they accompany
    /// (<c>payment_intent.succeeded</c> and <c>payment_intent.canceled</c>), so the second to arrive finds the work done.
    /// </summary>
    private async Task<IReadOnlyList<ServicePaymentNotice>> HandleServiceChargeEventAsync(Event stripeEvent, Charge charge)
    {
        var captured = stripeEvent.Type == "charge.captured";
        var paymentId = charge.Metadata.TryGetValue(ServiceCharges.PaymentMetadataKey, out var raw) && Guid.TryParse(raw, out var parsed)
            ? parsed
            : (Guid?)null;
        var holdId = charge.Metadata.TryGetValue(ServiceCharges.HoldMetadataKey, out var holdRaw) && Guid.TryParse(holdRaw, out var parsedHold)
            ? parsedHold
            : (Guid?)null;

        return await supplierPayments.ApplyPaymentIntentEventAsync(new ServicePaymentIntentEvent(
            stripeEvent.Id ?? string.Empty,
            captured ? "payment_intent.succeeded" : "payment_intent.canceled",
            charge.PaymentIntentId,
            stripeEvent.Account,
            paymentId,
            charge.Amount,
            captured ? charge.AmountCaptured : 0,
            charge.Currency,
            charge.ApplicationFeeAmount,
            charge.FailureCode,
            EventTime(stripeEvent),
            AmountCapturableCents: 0,
            charge.Id,
            holdId));
    }

    /// <summary>
    /// <c>transfer.created</c> and <c>transfer.reversed</c> (SP-15c-a): the transfers CasaZen makes to the suppliers carry the id of their
    /// payment in the metadata. A transfer made for anything else (the platform has other flows) is not ours: nothing happens.
    /// </summary>
    private async Task<IReadOnlyList<ServicePaymentNotice>> HandleServiceTransferAsync(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Transfer transfer
            || transfer.Metadata is null
            || !transfer.Metadata.TryGetValue("kind", out var kind)
            || !string.Equals(kind, ServiceCharges.TransferKind, StringComparison.Ordinal))
        {
            logger.LogInformation("Unhandled Stripe event: {EventType} (not a service transfer)", stripeEvent.Type);
            return [];
        }

        var paymentId = transfer.Metadata.TryGetValue(ServiceCharges.PaymentMetadataKey, out var raw) && Guid.TryParse(raw, out var parsed)
            ? parsed
            : (Guid?)null;
        var result = await supplierPayments.ApplyTransferEventAsync(new ServiceTransferEvent(
            stripeEvent.Id ?? string.Empty,
            stripeEvent.Type,
            transfer.Id,
            transfer.Amount,
            transfer.Currency,
            transfer.DestinationId,
            transfer.SourceTransactionId,
            transfer.TransferGroup,
            transfer.Reversed,
            paymentId));
        return result.Notices;
    }

    /// <summary>When Stripe says the event happened; null when the event has no creation time (synthetic events).</summary>
    private static DateTime? EventTime(Event stripeEvent) =>
        stripeEvent.Created.Year >= 2000 ? DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc) : null;

    private async Task<ServicePaymentEventResult> HandleServiceChargeRefundedAsync(Charge? charge, string? account) =>
        charge is null || string.IsNullOrWhiteSpace(charge.PaymentIntentId)
            ? ServicePaymentEventResult.NotOurs
            : await supplierPayments.ApplyChargeRefundedAsync(charge.PaymentIntentId, account);

    private async Task<ServicePaymentEventResult> HandleServiceRefundChangedAsync(Refund? refund, string? account) =>
        refund is null || string.IsNullOrWhiteSpace(refund.PaymentIntentId)
            ? ServicePaymentEventResult.NotOurs
            : await supplierPayments.ApplyRefundChangedAsync(StripeSupplierPaymentGateway.MapRefund(refund), account);

    private async Task<ServicePaymentEventResult> HandleServiceDisputeAsync(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Dispute dispute || string.IsNullOrWhiteSpace(dispute.PaymentIntentId))
            return ServicePaymentEventResult.NotOurs;

        var disputeEvent = new ServiceDisputeEvent(
            stripeEvent.Id ?? string.Empty,
            dispute.Id,
            dispute.PaymentIntentId,
            stripeEvent.Account,
            dispute.Amount,
            dispute.Currency,
            dispute.Reason,
            dispute.Status,
            stripeEvent.Type);

        // The created one keeps its own path (the error in the log and the e-mail to the admins, SP-15b); the later ones of the
        // money CasaZen holds only update what is recorded on the payment (SP-15c-a).
        return stripeEvent.Type == "charge.dispute.created"
            ? await supplierPayments.ApplyDisputeCreatedAsync(disputeEvent)
            : await supplierPayments.ApplyDisputeChangedAsync(disputeEvent);
    }

    /// <summary>
    /// A PaymentIntent succeeded. Rent charges have their own handler (deferred charges too, see
    /// <see cref="HandleDeferredChargeAsync"/>); the checkout's PaymentIntents (<c>direct-booking</c>) on the Connect endpoint, and every booking payment reported by the platform
    /// endpoint, are settled by <see cref="CheckoutPaymentSettlementService"/>: the booking is confirmed, confirmed again
    /// when the dates are still free, or refunded in full when they are not (BK-04, A3-04). Never "Completed" on a
    /// cancelled booking without one of the two.
    /// </summary>
    private async Task<CheckoutPaymentSettlement?> HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source, string? account)
    {
        if (paymentIntent is null)
            return null;

        TryGetMetadataKind(paymentIntent, out var kind);
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

    /// <summary>
    /// The card of a deferred payment is saved: the pending booking is confirmed (charged at the free-cancellation
    /// deadline). The confirmation emails follow the commit, once per transition (BK-10): the booking row is locked, so
    /// two different events of the same SetupIntent cannot both see it pending.
    /// </summary>
    private async Task<CheckoutPaymentSettlement?> HandleSetupIntentSucceededAsync(SetupIntent? setupIntent)
    {
        if (setupIntent is null)
            return null;

        logger.LogInformation("Direct booking setup intent succeeded: {SetupIntentId}", setupIntent.Id);

        if (!TryGetMetadataKind(setupIntent, out var kind) || !string.Equals(kind, "direct-booking-setup", StringComparison.Ordinal))
            return null;

        if (!setupIntent.Metadata.TryGetValue("bookingId", out var bookingIdRaw) || !Guid.TryParse(bookingIdRaw, out var bookingId))
        {
            logger.LogWarning("Setup intent has no bookingId metadata: {SetupIntentId}", setupIntent.Id);
            return null;
        }

        await LockBookingRowAsync(bookingId);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            logger.LogWarning("No booking found for setup intent: {BookingId}", bookingId);
            return null;
        }

        if (booking.Status != BookingStatus.Pending)
        {
            logger.LogWarning(
                "Ignoring setup intent {SetupIntentId} for booking {BookingId} in status {Status}",
                setupIntent.Id,
                bookingId,
                booking.Status);
            return null;
        }

        var paymentMethodId = setupIntent.PaymentMethodId;
        if (string.IsNullOrWhiteSpace(paymentMethodId))
        {
            logger.LogWarning("Setup intent has no payment method: {SetupIntentId}", setupIntent.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(setupIntent.CustomerId))
        {
            logger.LogWarning("Setup intent has no customer: {SetupIntentId}", setupIntent.Id);
            return null;
        }

        booking.StripePaymentMethodId = paymentMethodId;
        booking.StripeCustomerId = setupIntent.CustomerId;
        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;
        booking.ConfirmedAt = booking.UpdatedAt;
        await bookingRepository.UpdateAsync(booking);

        logger.LogInformation("Booking {BookingId} confirmed with payment method {PaymentMethodId}", bookingId, paymentMethodId);
        return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.ConfirmedWithSavedCard, bookingId);
    }

    /// <summary>Row lock of the booking for the rest of the event transaction; nothing outside PostgreSQL.</summary>
    private async Task LockBookingRowAsync(Guid bookingId)
    {
        if (!dbContext.Database.IsNpgsql())
            return;

        await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Bookings" WHERE "Id" = {bookingId} FOR UPDATE""")
            .ToListAsync();
    }

    private static bool IsRentCharge(PaymentIntent? paymentIntent) =>
        paymentIntent is not null &&
        TryGetMetadataKind(paymentIntent, out var kind) &&
        string.Equals(kind, RentCharges.Kind, StringComparison.Ordinal);

    /// <summary>
    /// An event of a rent installment PaymentIntent (LT-06, A7-07), from the Connect endpoint or the platform endpoint
    /// listening to connected accounts (the event's <c>account</c>): applied in the event transaction, never dropped as
    /// processed without effect. Events of the platform account itself are ignored (rent is charged on the landlord's
    /// connected account).
    /// </summary>
    private async Task<RentPaymentNotice?> HandleRentChargeAsync(
        PaymentIntent paymentIntent,
        string eventType,
        WebhookSource source,
        string? account)
    {
        var installmentId = paymentIntent.Metadata.TryGetValue(RentCharges.InstallmentMetadataKey, out var raw) &&
                            Guid.TryParse(raw, out var parsed)
            ? parsed
            : (Guid?)null;
        logger.LogInformation(
            "Rent payment intent {PaymentIntentId}: {EventType} (source={Source}, account={AccountId})",
            paymentIntent.Id,
            eventType,
            source,
            account ?? "none");
        return await rentBillingService.ApplyPaymentIntentEventAsync(new RentPaymentIntentEvent(
            paymentIntent.Id,
            eventType,
            account,
            installmentId,
            paymentIntent.Amount,
            paymentIntent.LastPaymentError?.Code));
    }

    private static bool IsDeferredCharge(PaymentIntent? paymentIntent) =>
        paymentIntent is not null &&
        TryGetMetadataKind(paymentIntent, out var kind) &&
        string.Equals(kind, DeferredCharges.Kind, StringComparison.Ordinal);

    /// <summary>
    /// Checks if a PaymentIntent is for a ConcordatoFacile purchase (ADR-005).
    /// CF payments are identified by the presence of "leaseId" in metadata (without a "kind" field).
    /// </summary>
    private static bool IsConcordatoFacilePayment(PaymentIntent? paymentIntent) =>
        paymentIntent is not null &&
        paymentIntent.Metadata is not null &&
        paymentIntent.Metadata.ContainsKey("leaseId") &&
        !paymentIntent.Metadata.ContainsKey("kind");

    /// <summary>
    /// Checks if a Charge refund is associated with a ConcordatoFacile purchase (ADR-005-R7).
    /// </summary>
    private async Task<bool> IsConcordatoFacileRefundAsync(Charge? charge)
    {
        if (charge is null || string.IsNullOrWhiteSpace(charge.PaymentIntentId))
            return false;

        return await dbContext.ConcordatoFacilePurchases
            .AnyAsync(p => p.StripePaymentIntentId == charge.PaymentIntentId);
    }

    /// <summary>
    /// An event of a deferred charge PaymentIntent ("Paga alla scadenza", BK-08, A3-14), from the Connect endpoint or the
    /// platform endpoint listening to connected accounts: succeeded, processing, failed (guest and host emailed once) or
    /// canceled. A success on a booking that is no longer confirmed is settled like a late checkout payment (BK-04).
    /// </summary>
    private async Task<(CheckoutPaymentSettlement? Settlement, DeferredChargeNotice? Notice)> HandleDeferredChargeAsync(
        PaymentIntent paymentIntent,
        string eventType,
        WebhookSource source,
        string? account)
    {
        var result = await deferredCharges.ApplyPaymentIntentEventAsync(paymentIntent, eventType, source, account);
        if (result.Outcome == DeferredChargeWebhookOutcome.SettleAsCheckoutPayment)
            return (await checkoutPayments.SettleSucceededPaymentAsync(paymentIntent.Id, source, account), null);

        return (null, result.Notice);
    }

    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent, WebhookSource source, string eventType)
    {
        if (paymentIntent is null)
            return;

        if (TryGetMetadataKind(paymentIntent, out var kind))
        {
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

    /// <summary>
    /// Checks if a Stripe Price ID is in the configured BiennialPriceIds array (ADR-006-R2).
    /// Returns false if the configuration is not available or the array is null/empty.
    /// </summary>
    private bool IsBiennialSubscriptionPrice(string priceId)
    {
        if (concordatoFacileOptions is null)
            return false;

        var biennialPriceIds = concordatoFacileOptions.Value.BiennialPriceIds;
        if (biennialPriceIds is null || biennialPriceIds.Length == 0)
            return false;

        return Array.Exists(biennialPriceIds, id => string.Equals(id, priceId, StringComparison.Ordinal));
    }
}
