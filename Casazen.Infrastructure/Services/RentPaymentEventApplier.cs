using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Applies payment events to a rent installment and decides which notice follows. Persistence, locks, Stripe requests
/// and delivery of the notices remain the responsibility of RentBillingService.
/// </summary>
internal sealed class RentPaymentEventApplier(TimeProvider clock, ILogger logger)
{
    public RentPaymentNotice? Apply(RentLedgerEntry entry, RentPaymentIntentEvent paymentEvent)
    {
        var isCurrent = string.Equals(entry.StripePaymentIntentId, paymentEvent.PaymentIntentId, StringComparison.Ordinal);
        var now = clock.GetUtcNow().UtcDateTime;
        switch (paymentEvent.EventType)
        {
            case "payment_intent.succeeded":
                if (entry.Status == RentLedgerStatus.Paid)
                {
                    if (!(entry.PaidVia == RentPaymentChannel.Stripe && isCurrent))
                    {
                        logger.LogError(
                            "Rent installment {InstallmentId} already paid ({PaidVia}) received payment intent {PaymentIntentId}: refund one of them from the Stripe dashboard",
                            entry.Id,
                            entry.PaidVia,
                            paymentEvent.PaymentIntentId);
                    }

                    return null;
                }

                if (entry.Status == RentLedgerStatus.Cancelled)
                {
                    logger.LogWarning(
                        "Rent installment {InstallmentId} was cancelled but payment intent {PaymentIntentId} succeeded: recorded as paid",
                        entry.Id,
                        paymentEvent.PaymentIntentId);
                }

                entry.Status = RentLedgerStatus.Paid;
                entry.PaidVia = RentPaymentChannel.Stripe;
                entry.PaidOn = clock.TodayInRomeAsDateOnly();
                entry.PaidAt = now;
                entry.StripePaymentIntentId = paymentEvent.PaymentIntentId;
                entry.FailureCode = null;
                entry.UpdatedAt = now;
                return new RentPaymentNotice(entry.Id, RentPaymentNoticeKind.Received);

            case "payment_intent.processing":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                entry.Status = RentLedgerStatus.Processing;
                entry.UpdatedAt = now;
                return null;

            case "payment_intent.payment_failed":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                var wasInFlight = entry.Status == RentLedgerStatus.Processing;
                entry.Status = RentLedgerStatus.Failed;
                entry.FailureCode = paymentEvent.FailureCode is { Length: > 100 } code ? code[..100] : paymentEvent.FailureCode;
                entry.LastFailedAt = now;
                entry.UpdatedAt = now;
                // On-session failures are shown to the tenant on the page; a payment that fails after it was accepted
                // (e.g. a SEPA debit returned) gets an email with a new link.
                return wasInFlight ? new RentPaymentNotice(entry.Id, RentPaymentNoticeKind.Failed) : null;

            case "payment_intent.canceled":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                entry.StripePaymentIntentId = null;
                entry.Status = entry.Status == RentLedgerStatus.Failed ? RentLedgerStatus.Failed : RentLedgerStatus.Scheduled;
                entry.UpdatedAt = now;
                return null;

            default:
                return null;
        }
    }
}
