using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICheckoutPaymentReconciliationService"/>
/// <remarks>
/// A succeeded payment goes through <see cref="CheckoutPaymentSettlementService"/>, the one the webhook uses, inside one
/// transaction (it takes its locks there); the emails follow the commit. It is idempotent with the webhook: whichever comes
/// second finds the payment <see cref="PaymentStatus.Completed"/> and does nothing. Only the checkout's own PaymentIntents
/// (<c>kind = direct-booking</c>) are touched, like on the Connect endpoint. One payment failing never stops the others.
/// </remarks>
public sealed class CheckoutPaymentReconciliationService(
    AppDbContext db,
    IStripeService stripeService,
    CheckoutPaymentSettlementService settlement,
    ILogger<CheckoutPaymentReconciliationService> logger,
    TimeProvider? timeProvider = null) : ICheckoutPaymentReconciliationService
{
    private const string ResourceMissingCode = "resource_missing";
    private const string DirectBookingKind = "direct-booking";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private enum Outcome
    {
        Settled,
        StillInFlight,
        Released,
        Failed,
    }

    public async Task<CheckoutPaymentReconciliationRun> ReconcileInFlightPaymentsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = _clock.GetUtcNow().UtcDateTime.AddMinutes(-ICheckoutPaymentReconciliationService.GraceMinutes);

        var paymentIds = await db.Payments
            .AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Processing &&
                        p.UpdatedAt < staleBefore &&
                        p.Booking.Status == BookingStatus.Pending &&
                        p.Booking.Source == BookingSource.Direct &&
                        p.Booking.PaymentOption != PaymentOption.OnSite)
            .OrderBy(p => p.UpdatedAt)
            .Select(p => p.Id)
            .Take(ICheckoutPaymentReconciliationService.BatchSize)
            .ToListAsync(cancellationToken);
        if (paymentIds.Count == 0)
            return CheckoutPaymentReconciliationRun.Empty;

        int settled = 0, stillInFlight = 0, released = 0, failed = 0;
        foreach (var paymentId in paymentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await ReconcileAsync(paymentId, cancellationToken))
                {
                    case Outcome.Settled:
                        settled++;
                        break;
                    case Outcome.StillInFlight:
                        stillInFlight++;
                        break;
                    case Outcome.Released:
                        released++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Payment {PaymentId} in flight could not be reconciled with Stripe; the next run retries it", paymentId);
                db.ChangeTracker.Clear();
            }
        }

        return new CheckoutPaymentReconciliationRun(settled, stillInFlight, released, failed);
    }

    private async Task<Outcome> ReconcileAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        var paymentIntentId = payment is null ? null : PaymentRefundService.PaymentIntentIdOf(payment);
        if (payment is null || paymentIntentId is null)
            return Outcome.Failed;

        var accountId = payment.StripeAccountId ?? await db.Orgs
            .Where(o => o.Id == payment.OrgId)
            .Select(o => o.StripeConnectedAccountId)
            .SingleOrDefaultAsync(cancellationToken);

        PaymentIntent? intent = null;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            try
            {
                intent = await stripeService.GetPaymentIntentAsync(paymentIntentId, accountId, cancellationToken);
            }
            catch (StripeException ex) when (ex.StripeError?.Code == ResourceMissingCode)
            {
                logger.LogWarning(
                    "Payment intent {PaymentIntentId} of payment {PaymentId} is unknown to Stripe on {AccountId}: it was never paid",
                    paymentIntentId,
                    paymentId,
                    accountId);
            }
            catch (StripeException ex)
            {
                logger.LogWarning(ex, "Payment intent {PaymentIntentId} could not be read from Stripe; the next run retries it", paymentIntentId);
                return Outcome.Failed;
            }
        }

        switch (intent?.Status)
        {
            case "succeeded" when intent.Metadata is not null &&
                                  intent.Metadata.TryGetValue("kind", out var kind) &&
                                  string.Equals(kind, DirectBookingKind, StringComparison.Ordinal):
                return await SettleAsync(paymentIntentId, accountId, cancellationToken);

            case "processing" or "requires_capture":
                return Outcome.StillInFlight;

            case "succeeded":
                // Not a payment of the checkout: nothing here to confirm. Left as it is, and counted so it shows in the log.
                logger.LogWarning("Payment intent {PaymentIntentId} succeeded but is not a direct booking payment: left alone", paymentIntentId);
                return Outcome.StillInFlight;

            default:
                // Never paid, canceled, or unknown to Stripe: back to pending, the expiry job cancels the intent and the booking.
                await db.Payments
                    .Where(p => p.Id == paymentId && p.Status == PaymentStatus.Processing)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(p => p.Status, PaymentStatus.Pending)
                              .SetProperty(p => p.UpdatedAt, _clock.GetUtcNow().UtcDateTime),
                        cancellationToken);
                logger.LogWarning(
                    "Payment intent {PaymentIntentId} of payment {PaymentId} is {Status} on Stripe: back to pending, the booking expires with the next run",
                    paymentIntentId,
                    paymentId,
                    intent?.Status ?? "missing");
                return Outcome.Released;
        }
    }

    private async Task<Outcome> SettleAsync(string paymentIntentId, string? accountId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        CheckoutPaymentSettlement result;
        try
        {
            result = await settlement.SettleSucceededPaymentAsync(paymentIntentId, WebhookSource.Connected, accountId, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }

        logger.LogWarning(
            "Payment intent {PaymentIntentId} succeeded on Stripe but the webhook had not settled it: {Outcome} for booking {BookingId}",
            paymentIntentId,
            result.Outcome,
            result.BookingId);
        await settlement.CompleteAsync(result, cancellationToken);
        return Outcome.Settled;
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(cancellationToken);
    }
}
