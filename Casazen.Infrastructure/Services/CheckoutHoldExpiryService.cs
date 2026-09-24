using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICheckoutHoldExpiryService"/>
/// <remarks>
/// Each hold is handled in its own READ COMMITTED transaction that holds the booking row
/// (<c>FOR UPDATE SKIP LOCKED</c>) for the Stripe calls: a concurrent run skips it, and once the first run has committed
/// the hold is no longer expired when read again. A Stripe failure leaves the hold as it is for the next run. The
/// idempotency key of a cancellation is derived from the intent and its latest attempt, so a retry never sends a
/// second cancellation of the same attempt. Without PostgreSQL (EF InMemory in unit tests) nothing is locked.
/// </remarks>
public sealed class CheckoutHoldExpiryService(
    AppDbContext db,
    IStripeService stripeService,
    IConfiguration configuration,
    ILogger<CheckoutHoldExpiryService> logger,
    TimeProvider? timeProvider = null) : ICheckoutHoldExpiryService
{
    private const string CanceledStatus = "canceled";
    private const string ResourceMissingCode = "resource_missing";
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";
    private const string SetupIntentUnexpectedStateCode = "setup_intent_unexpected_state";

    /// <summary>The guest has completed the payment step: the webhook confirms the booking (or BK-04 handles it).</summary>
    private static readonly HashSet<string> PaymentInFlightStatuses =
        new(StringComparer.Ordinal) { "succeeded", "processing", "requires_capture" };

    /// <summary>The card is saved or being verified: the <c>setup_intent.succeeded</c> webhook confirms the booking.</summary>
    private static readonly HashSet<string> SetupInFlightStatuses = new(StringComparer.Ordinal) { "succeeded", "processing" };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private enum HoldOutcome
    {
        Expired,
        LeftToWebhook,
        Skipped,
        Failed,
    }

    private enum IntentRelease
    {
        Released,
        InFlight,
        Failed,
    }

    public Task<CheckoutHoldExpiryRun> ExpireDueHoldsAsync(CancellationToken cancellationToken = default) =>
        ExpireAsync(scope: null, cancellationToken);

    public Task<CheckoutHoldExpiryRun> ExpireOverlappingHoldsAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken cancellationToken = default)
    {
        var checkInDate = checkIn.Date;
        var checkOutDate = checkOut.Date;
        return ExpireAsync(
            holds => holds.Where(b => b.PropertyId == propertyId &&
                                      b.CheckInDate.Date < checkOutDate &&
                                      b.CheckOutDate.Date > checkInDate),
            cancellationToken);
    }

    private async Task<CheckoutHoldExpiryRun> ExpireAsync(
        Func<IQueryable<Booking>, IQueryable<Booking>>? scope,
        CancellationToken cancellationToken)
    {
        var cutoffUtc = CheckoutHolds.ExpiryCutoffUtc(UtcNow(), CheckoutHolds.GetTtlMinutes(configuration));
        var holds = db.Bookings.Where(CheckoutHolds.IsExpired(cutoffUtc));
        if (scope is not null)
            holds = scope(holds);

        var holdIds = await holds
            .OrderBy(b => b.CreatedAt)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);
        if (holdIds.Count == 0)
            return CheckoutHoldExpiryRun.Empty;

        int expired = 0, leftToWebhook = 0, skipped = 0, failed = 0;
        foreach (var bookingId in holdIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await ExpireHoldAsync(bookingId, cutoffUtc, cancellationToken))
            {
                case HoldOutcome.Expired:
                    expired++;
                    break;
                case HoldOutcome.LeftToWebhook:
                    leftToWebhook++;
                    break;
                case HoldOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new CheckoutHoldExpiryRun(expired, leftToWebhook, skipped, failed);
    }

    private async Task<HoldOutcome> ExpireHoldAsync(Guid bookingId, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        Booking? booking = null;
        try
        {
            if (!await TryLockBookingAsync(bookingId, cancellationToken))
            {
                logger.LogInformation("Checkout hold {BookingId} is being expired by another run: skipped", bookingId);
                return HoldOutcome.Skipped;
            }

            // Read again under the lock: a concurrent run, the payment webhook or the host may have changed it.
            booking = await db.Bookings
                .Where(b => b.Id == bookingId)
                .Where(CheckoutHolds.IsExpired(cutoffUtc))
                .Include(b => b.Payments)
                .SingleOrDefaultAsync(cancellationToken);
            if (booking is null)
                return HoldOutcome.Skipped;

            var connectedAccountId = await db.Orgs
                .Where(o => o.Id == booking.OrgId)
                .Select(o => o.StripeConnectedAccountId)
                .SingleOrDefaultAsync(cancellationToken);

            var (release, inFlightPaymentIntentIds) = await ReleaseIntentsAsync(booking, connectedAccountId, cancellationToken);
            switch (release)
            {
                case IntentRelease.Released:
                    ExpireBooking(booking);
                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "Checkout hold {BookingId} expired: intent cancelled on Stripe, dates released", booking.Id);
                    return HoldOutcome.Expired;

                case IntentRelease.InFlight:
                    MarkPaymentsInFlight(booking, inFlightPaymentIntentIds);
                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);
                    return HoldOutcome.LeftToWebhook;

                default:
                    return HoldOutcome.Failed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checkout hold {BookingId} could not be expired; the next run retries it", bookingId);
            ForgetChanges(booking);
            return HoldOutcome.Failed;
        }
    }

    private async Task<(IntentRelease Release, IReadOnlyCollection<string> InFlightPaymentIntentIds)> ReleaseIntentsAsync(
        Booking booking,
        string? connectedAccountId,
        CancellationToken cancellationToken)
    {
        var inFlight = new List<string>();
        var failed = false;

        // Intents a guest may still pay: the payment rows not settled otherwise (Failed = declined, retryable on the same intent).
        var paymentIntentIds = booking.Payments
            .Where(p => p.StripePaymentIntentId != null &&
                        (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Failed))
            .Select(p => p.StripePaymentIntentId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var paymentIntentId in paymentIntentIds)
        {
            switch (await ReleasePaymentIntentAsync(booking.Id, paymentIntentId, connectedAccountId, cancellationToken))
            {
                case IntentRelease.InFlight:
                    inFlight.Add(paymentIntentId);
                    break;
                case IntentRelease.Failed:
                    failed = true;
                    break;
            }
        }

        var setupInFlight = false;
        if (!string.IsNullOrWhiteSpace(booking.StripeSetupIntentId))
        {
            switch (await ReleaseSetupIntentAsync(booking.Id, booking.StripeSetupIntentId, connectedAccountId, cancellationToken))
            {
                case IntentRelease.InFlight:
                    setupInFlight = true;
                    break;
                case IntentRelease.Failed:
                    failed = true;
                    break;
            }
        }

        if (failed)
            return (IntentRelease.Failed, inFlight);
        if (inFlight.Count > 0 || setupInFlight)
            return (IntentRelease.InFlight, inFlight);
        return (IntentRelease.Released, inFlight);
    }

    private async Task<IntentRelease> ReleasePaymentIntentAsync(
        Guid bookingId,
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectedAccountId))
        {
            logger.LogWarning(
                "Checkout hold {BookingId}: the org has no connected Stripe account, payment intent {PaymentIntentId} cannot be cancelled; releasing the dates",
                bookingId,
                paymentIntentId);
            return IntentRelease.Released;
        }

        PaymentIntent intent;
        try
        {
            intent = await stripeService.GetPaymentIntentAsync(paymentIntentId, connectedAccountId, cancellationToken);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == ResourceMissingCode)
        {
            logger.LogWarning(
                "Checkout hold {BookingId}: payment intent {PaymentIntentId} not found on {AccountId}; releasing the dates",
                bookingId,
                paymentIntentId,
                connectedAccountId);
            return IntentRelease.Released;
        }

        var release = ClassifyPaymentIntent(bookingId, intent);
        if (release != null)
            return release.Value;

        try
        {
            var idempotencyKey = $"checkout-hold-expiry:{bookingId}:{paymentIntentId}:{intent.Status}:{intent.LatestChargeId ?? "none"}";
            var canceled = await stripeService.CancelPaymentIntentAsync(
                paymentIntentId, connectedAccountId, idempotencyKey, cancellationToken);
            if (string.Equals(canceled.Status, CanceledStatus, StringComparison.Ordinal))
                return IntentRelease.Released;

            logger.LogWarning(
                "Checkout hold {BookingId}: payment intent {PaymentIntentId} is {Status} after the cancellation; retried by the next run",
                bookingId,
                paymentIntentId,
                canceled.Status);
            return IntentRelease.Failed;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
        {
            // The guest paid between the read and the cancellation: read the final state again.
            var current = await stripeService.GetPaymentIntentAsync(paymentIntentId, connectedAccountId, cancellationToken);
            return ClassifyPaymentIntent(bookingId, current) ?? IntentRelease.Failed;
        }
    }

    /// <summary>Released when already canceled, InFlight when the guest has paid or is paying, null when it can be cancelled.</summary>
    private IntentRelease? ClassifyPaymentIntent(Guid bookingId, PaymentIntent intent)
    {
        if (string.Equals(intent.Status, CanceledStatus, StringComparison.Ordinal))
            return IntentRelease.Released;

        if (intent.Status is not null && PaymentInFlightStatuses.Contains(intent.Status))
        {
            logger.LogWarning(
                "Checkout hold {BookingId} not expired: payment intent {PaymentIntentId} is {Status}; left to the payment webhook",
                bookingId,
                intent.Id,
                intent.Status);
            return IntentRelease.InFlight;
        }

        return null;
    }

    private async Task<IntentRelease> ReleaseSetupIntentAsync(
        Guid bookingId,
        string setupIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectedAccountId))
        {
            logger.LogWarning(
                "Checkout hold {BookingId}: the org has no connected Stripe account, setup intent {SetupIntentId} cannot be cancelled; releasing the dates",
                bookingId,
                setupIntentId);
            return IntentRelease.Released;
        }

        SetupIntent intent;
        try
        {
            intent = await stripeService.GetSetupIntentAsync(setupIntentId, connectedAccountId, cancellationToken);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == ResourceMissingCode)
        {
            logger.LogWarning(
                "Checkout hold {BookingId}: setup intent {SetupIntentId} not found on {AccountId}; releasing the dates",
                bookingId,
                setupIntentId,
                connectedAccountId);
            return IntentRelease.Released;
        }

        var release = ClassifySetupIntent(bookingId, intent);
        if (release != null)
            return release.Value;

        try
        {
            var idempotencyKey = $"checkout-hold-expiry:{bookingId}:{setupIntentId}:{intent.Status}:{intent.LatestAttemptId ?? "none"}";
            var canceled = await stripeService.CancelSetupIntentAsync(
                setupIntentId, connectedAccountId, idempotencyKey, cancellationToken);
            if (string.Equals(canceled.Status, CanceledStatus, StringComparison.Ordinal))
                return IntentRelease.Released;

            logger.LogWarning(
                "Checkout hold {BookingId}: setup intent {SetupIntentId} is {Status} after the cancellation; retried by the next run",
                bookingId,
                setupIntentId,
                canceled.Status);
            return IntentRelease.Failed;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == SetupIntentUnexpectedStateCode)
        {
            var current = await stripeService.GetSetupIntentAsync(setupIntentId, connectedAccountId, cancellationToken);
            return ClassifySetupIntent(bookingId, current) ?? IntentRelease.Failed;
        }
    }

    private IntentRelease? ClassifySetupIntent(Guid bookingId, SetupIntent intent)
    {
        if (string.Equals(intent.Status, CanceledStatus, StringComparison.Ordinal))
            return IntentRelease.Released;

        if (intent.Status is not null && SetupInFlightStatuses.Contains(intent.Status))
        {
            logger.LogWarning(
                "Checkout hold {BookingId} not expired: setup intent {SetupIntentId} is {Status}; left to the setup webhook",
                bookingId,
                intent.Id,
                intent.Status);
            return IntentRelease.InFlight;
        }

        return null;
    }

    private void ExpireBooking(Booking booking)
    {
        var now = UtcNow();
        booking.Status = BookingStatus.Cancelled;
        booking.CancellationReason = BookingCancellationReason.CheckoutHoldExpired;
        booking.UpdatedAt = now;

        // Nothing is collected any more: same state the payment_intent.canceled webhook records.
        foreach (var payment in booking.Payments.Where(p => p.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Failed;
            payment.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Records Stripe's answer on the payment rows (<see cref="PaymentStatus.Processing"/>): the hold then keeps its dates
    /// (<see cref="CheckoutHolds.IsExpired"/>) until the webhook completes it (confirmed) or fails it (expired again).
    /// </summary>
    private void MarkPaymentsInFlight(Booking booking, IReadOnlyCollection<string> paymentIntentIds)
    {
        var now = UtcNow();
        foreach (var payment in booking.Payments.Where(p =>
                     p.StripePaymentIntentId != null &&
                     paymentIntentIds.Contains(p.StripePaymentIntentId) &&
                     p.Status != PaymentStatus.Completed))
        {
            payment.Status = PaymentStatus.Processing;
            payment.UpdatedAt = now;
        }
    }

    /// <summary>Changes of a hold that failed to save must not be written later by another save of the same context.</summary>
    private void ForgetChanges(Booking? booking)
    {
        if (booking is null)
            return;

        foreach (var payment in booking.Payments)
            db.Entry(payment).State = EntityState.Detached;
        db.Entry(booking).State = EntityState.Detached;
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>Row lock on the booking for the rest of the transaction; false when another transaction holds it.</summary>
    private async Task<bool> TryLockBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return true;

        var locked = await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Bookings" WHERE "Id" = {bookingId} FOR UPDATE SKIP LOCKED""")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
