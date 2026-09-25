using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the payment webhook did with a succeeded PaymentIntent of a booking payment (BK-04), or with the SetupIntent of
/// a deferred payment (<see cref="ConfirmedWithSavedCard"/>, BK-10).
/// </summary>
public enum CheckoutPaymentOutcome
{
    /// <summary>No payment row for the PaymentIntent, or the event came from another Stripe account: nothing changed.</summary>
    Ignored,

    /// <summary>Already applied: the payment was completed on a valid booking, refunded, or its late refund exists.</summary>
    AlreadySettled,

    /// <summary>Payment completed on a booking that was already confirmed (or checked in / out).</summary>
    Recorded,

    /// <summary>The checkout hold was still valid: booking confirmed.</summary>
    Confirmed,

    /// <summary>The hold had expired or the booking was cancelled, and the dates were still free: booking confirmed again.</summary>
    Reconfirmed,

    /// <summary>The dates were no longer free: booking cancelled, full refund reserved (sent to Stripe after the commit).</summary>
    Refunding,

    /// <summary>
    /// Deferred payment: the guest's card was saved (<c>setup_intent.succeeded</c>) and the booking confirmed; it is
    /// charged at the free-cancellation deadline (BK-10).
    /// </summary>
    ConfirmedWithSavedCard,
}

/// <summary>Result of <see cref="CheckoutPaymentSettlementService.SettleSucceededPaymentAsync"/>.</summary>
public sealed record CheckoutPaymentSettlement(CheckoutPaymentOutcome Outcome, Guid? BookingId = null, Guid? RefundId = null)
{
    public static CheckoutPaymentSettlement Ignored { get; } = new(CheckoutPaymentOutcome.Ignored);
}

/// <summary>
/// Applies a succeeded PaymentIntent of a booking payment, on the Connect or the platform webhook endpoint (BK-04,
/// A3-04). A payment can succeed after its checkout hold expired (3-D Secure plus a banking app can take longer than
/// <c>DirectBooking:PendingTtlMinutes</c>), or on a booking already cancelled. It is never recorded as a bare
/// "Completed" on a cancelled booking:
/// <list type="bullet">
///   <item>hold still valid, or dates still free (no confirmed booking, valid hold or iCal block on them): the booking is
///   confirmed, and when it had expired or been cancelled the guest gets a confirmation email;</item>
///   <item>otherwise the booking stays (or becomes) cancelled and the whole payment is refunded through
///   <see cref="PaymentRefundService"/> (BK-02) on the account the PaymentIntent lives on, with the idempotency key
///   <c>late-payment-refund:{PaymentIntentId}</c>; the guest is emailed once Stripe confirms the refund.</item>
/// </list>
/// </summary>
/// <remarks>
/// Runs inside the transaction of the webhook event (PL-10), so the claim of the event, the payment, the booking and the
/// refund reservation commit together and a duplicate event is skipped. On PostgreSQL it first takes, in this order,
/// the property lock of the booking checks (<see cref="BookingRepository.LockPropertyDatesAsync"/>: a concurrent
/// checkout of the same dates waits and then sees the outcome), the booking-cancellation and payment-refund advisory
/// locks of BK-02 (a host cancellation waits), and the booking row (<c>FOR UPDATE</c>: the hold-expiry job skips it).
/// The Stripe refund call and the emails happen after the commit (<see cref="CompleteAsync"/>); a refund whose call does
/// not happen then is resent by <c>PaymentRefundSubmitJob</c>, scheduled before the commit. The emails follow the
/// transition, not the event: a duplicate or later event of a payment already settled is <see cref="CheckoutPaymentOutcome.AlreadySettled"/>
/// and sends nothing.
/// </remarks>
public sealed class CheckoutPaymentSettlementService(
    AppDbContext db,
    IPaymentRepository paymentRepository,
    PaymentRefundService refundService,
    IPaymentRefundRetryScheduler refundRetryScheduler,
    BookingNotifier notifier,
    IConfiguration configuration,
    ILogger<CheckoutPaymentSettlementService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Settles the payment of <paramref name="paymentIntentId"/>. <paramref name="eventAccountId"/> is the event's
    /// <c>account</c> (the connected account of a direct charge); on the platform endpoint without one the PaymentIntent
    /// lives on the platform account. Call <see cref="CompleteAsync"/> with the result once the event is committed.
    /// </summary>
    public async Task<CheckoutPaymentSettlement> SettleSucceededPaymentAsync(
        string paymentIntentId,
        WebhookSource source,
        string? eventAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        var payment = await paymentRepository.GetByTransactionIdAsync(paymentIntentId);
        if (payment is null)
        {
            logger.LogWarning("No payment row for payment intent {PaymentIntentId} (source={Source})", paymentIntentId, source);
            return CheckoutPaymentSettlement.Ignored;
        }

        await LockAsync(payment, cancellationToken);

        // Read again under the locks: a checkout of the same dates, the expiry job or the host may have changed them.
        await db.Entry(payment).ReloadAsync(cancellationToken);
        await db.Entry(payment).Reference(p => p.Booking).LoadAsync(cancellationToken);
        var booking = payment.Booking;
        await db.Entry(booking).ReloadAsync(cancellationToken);

        if (!TryAcceptAccount(payment, source, eventAccountId, out var onPlatform))
            return CheckoutPaymentSettlement.Ignored;

        var lateRefundKey = PaymentRefundService.LatePaymentRefundKey(paymentIntentId);
        if (await db.PaymentRefunds.AnyAsync(r => r.PaymentId == payment.Id && r.IdempotencyKey == lateRefundKey, cancellationToken))
        {
            logger.LogInformation(
                "Payment intent {PaymentIntentId} of booking {BookingId} is already being refunded: nothing to do",
                paymentIntentId,
                booking.Id);
            return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.AlreadySettled, booking.Id);
        }

        if (payment.Status is PaymentStatus.Refunded or PaymentStatus.PartiallyRefunded)
        {
            logger.LogInformation(
                "Payment intent {PaymentIntentId} of booking {BookingId} was already refunded ({Status}): nothing to do",
                paymentIntentId,
                booking.Id,
                payment.Status);
            return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.AlreadySettled, booking.Id);
        }

        if (booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut)
        {
            if (payment.Status == PaymentStatus.Completed)
                return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.AlreadySettled, booking.Id);

            MarkCompleted(payment, paymentIntentId, eventAccountId, onPlatform);
            await paymentRepository.UpdateAsync(payment);
            return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.Recorded, booking.Id);
        }

        var now = UtcNow;
        var expiredHoldCutoff = CheckoutHolds.CutoffAt(now, CheckoutHolds.GetTtlMinutes(configuration));

        // A hold still within its TTL (or whose payment the expiry job saw in flight) kept its dates in the iCal export:
        // only another booking can stand in its way. An expired hold or a cancelled booking had released them.
        var validHold = booking.Status == BookingStatus.Pending &&
            !await db.Bookings
                .Where(b => b.Id == booking.Id)
                .Where(CheckoutHolds.IsExpired(expiredHoldCutoff))
                .AnyAsync(cancellationToken);
        var datesFree = await AreDatesFreeAsync(booking, expiredHoldCutoff, checkCalendarBlocks: !validHold, cancellationToken);

        MarkCompleted(payment, paymentIntentId, eventAccountId, onPlatform);
        await paymentRepository.UpdateAsync(payment);

        if (datesFree)
        {
            var previousStatus = booking.Status;
            booking.Status = BookingStatus.Confirmed;
            booking.CancellationReason = null;
            booking.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            if (validHold)
                return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.Confirmed, booking.Id);

            logger.LogWarning(
                "Payment intent {PaymentIntentId} succeeded on booking {BookingId} after its hold ended ({PreviousStatus}); the dates were free: booking confirmed again",
                paymentIntentId,
                booking.Id,
                previousStatus);
            return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.Reconfirmed, booking.Id);
        }

        if (booking.Status == BookingStatus.Pending)
        {
            booking.Status = BookingStatus.Cancelled;
            booking.CancellationReason = BookingCancellationReason.DatesUnavailableAtPayment;
            booking.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        var refunds = await db.PaymentRefunds.Where(r => r.PaymentId == payment.Id).ToListAsync(cancellationToken);
        if (PaymentRefundService.Summarize(payment, refunds).RefundableAmount <= 0)
        {
            // Refunded from the Stripe Dashboard before this event arrived: only the payment status is brought up to date.
            await refundService.RecomputePaymentAsync(payment, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Payment intent {PaymentIntentId} succeeded on booking {BookingId} whose dates are taken, but nothing is left to refund",
                paymentIntentId,
                booking.Id);
            return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.AlreadySettled, booking.Id);
        }

        var refund = await refundService.ReserveAsync(
            payment,
            amount: null,
            PaymentRefundOrigin.BookingCancellation,
            reason: null,
            requestedByUserId: null,
            cancellationToken,
            lateRefundKey);

        // Safety net: if the process stops between the commit and the Stripe call, the job sends the refund (same key).
        refundRetryScheduler.ScheduleSubmit(refund.Id);

        logger.LogWarning(
            "Payment intent {PaymentIntentId} succeeded on booking {BookingId} when its dates were no longer free: booking cancelled, full refund {RefundId} of {Amount} EUR reserved",
            paymentIntentId,
            booking.Id,
            refund.Id,
            refund.Amount);
        return new CheckoutPaymentSettlement(CheckoutPaymentOutcome.Refunding, booking.Id, refund.Id);
    }

    /// <summary>
    /// After the webhook event is committed: sends the reserved refund to Stripe (the guest is emailed when Stripe
    /// confirms it) or queues the emails of a booking that has just been confirmed (BK-10). Never throws: a refund that
    /// could not be sent is resent by the job scheduled with it.
    /// </summary>
    public async Task CompleteAsync(CheckoutPaymentSettlement settlement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        try
        {
            switch (settlement.Outcome)
            {
                case CheckoutPaymentOutcome.Confirmed when settlement.BookingId is { } bookingId:
                    await notifier.BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline, cancellationToken);
                    break;
                case CheckoutPaymentOutcome.Reconfirmed when settlement.BookingId is { } bookingId:
                    await notifier.BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnlineLate, cancellationToken);
                    break;
                case CheckoutPaymentOutcome.ConfirmedWithSavedCard when settlement.BookingId is { } bookingId:
                    await notifier.BookingConfirmedAsync(bookingId, BookingConfirmationKind.DeferredCharge, cancellationToken);
                    break;
                case CheckoutPaymentOutcome.Refunding when settlement.RefundId is { } refundId:
                    await SubmitRefundAsync(refundId, settlement.BookingId, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Payment of booking {BookingId}: {Outcome} committed, follow-up failed (a refund {RefundId} is resent by its job)",
                settlement.BookingId,
                settlement.Outcome,
                settlement.RefundId);
        }
    }

    private async Task SubmitRefundAsync(Guid refundId, Guid? bookingId, CancellationToken cancellationToken)
    {
        var refund = await db.PaymentRefunds.FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);
        if (refund is null)
            return;

        try
        {
            refund = await refundService.SubmitAsync(refund, throwOnTransientFailure: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Refund {RefundId} of booking {BookingId} not confirmed by Stripe yet: resent by its job", refundId, bookingId);
            return;
        }

        if (refund.Status is PaymentRefundStatus.Failed or PaymentRefundStatus.Canceled)
        {
            logger.LogError(
                "Automatic refund {RefundId} of the late payment of booking {BookingId} was not made by Stripe ({FailureReason}): refund it from the payment page",
                refundId,
                bookingId,
                refund.FailureReason);
        }
    }

    /// <summary>
    /// No other booking takes the dates (confirmed, checked in, or a hold still valid:
    /// <see cref="CheckoutHolds.OccupiesDates"/>) and, when <paramref name="checkCalendarBlocks"/>, no iCal block.
    /// </summary>
    private async Task<bool> AreDatesFreeAsync(
        Booking booking,
        HoldExpiryCutoff expiredHoldCutoff,
        bool checkCalendarBlocks,
        CancellationToken cancellationToken)
    {
        var checkIn = booking.CheckInDate.Date;
        var checkOut = booking.CheckOutDate.Date;

        var takenByBooking = await db.Bookings
            .Where(b => b.PropertyId == booking.PropertyId &&
                        b.Id != booking.Id &&
                        b.CheckInDate.Date < checkOut &&
                        b.CheckOutDate.Date > checkIn)
            .Where(CheckoutHolds.OccupiesDates(expiredHoldCutoff))
            .AnyAsync(cancellationToken);
        if (takenByBooking)
            return false;

        return !checkCalendarBlocks ||
               !await PropertyICalSyncService.HasOverlappingBlockAsync(db, booking.PropertyId, checkIn, checkOut, cancellationToken);
    }

    /// <summary>
    /// The event must come from the account the PaymentIntent lives on: the Connect endpoint carries the connected
    /// account (it must match the one stored at creation), the platform endpoint without one means a PaymentIntent of
    /// the platform account.
    /// </summary>
    private bool TryAcceptAccount(Payment payment, WebhookSource source, string? eventAccountId, out bool onPlatform)
    {
        onPlatform = false;
        if (!string.IsNullOrWhiteSpace(eventAccountId))
        {
            if (!payment.StripeIntentOnPlatform &&
                (string.IsNullOrWhiteSpace(payment.StripeAccountId) ||
                 string.Equals(payment.StripeAccountId, eventAccountId, StringComparison.Ordinal)))
                return true;

            logger.LogWarning(
                "Payment intent of payment {PaymentId} reported by account {EventAccount}, expected {ExpectedAccount}: ignored",
                payment.Id,
                eventAccountId,
                payment.StripeIntentOnPlatform ? "platform" : payment.StripeAccountId);
            return false;
        }

        if (source != WebhookSource.Platform)
            return true; // Stripe always sends the account on the Connect endpoint; kept as the payment knows it.

        if (!string.IsNullOrWhiteSpace(payment.StripeAccountId))
        {
            logger.LogWarning(
                "Payment intent of payment {PaymentId} reported by the platform account, expected {ExpectedAccount}: ignored",
                payment.Id,
                payment.StripeAccountId);
            return false;
        }

        onPlatform = true;
        return true;
    }

    private void MarkCompleted(Payment payment, string paymentIntentId, string? eventAccountId, bool onPlatform)
    {
        var now = UtcNow;
        if (payment.Status != PaymentStatus.Completed)
        {
            payment.Status = PaymentStatus.Completed;
            payment.ProcessedAt = now;
        }

        payment.StripePaymentIntentId ??= paymentIntentId;
        if (!string.IsNullOrWhiteSpace(eventAccountId))
            payment.StripeAccountId ??= eventAccountId;
        if (onPlatform)
            payment.StripeIntentOnPlatform = true;
        payment.UpdatedAt = now;
    }

    /// <summary>Property, booking-cancellation and payment-refund locks, then the booking row; nothing outside PostgreSQL.</summary>
    private async Task LockAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        var propertyId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == payment.BookingId)
            .Select(b => b.PropertyId)
            .SingleAsync(cancellationToken);
        await BookingRepository.LockPropertyDatesAsync(db, propertyId, cancellationToken);

        var ownTransaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.BookingCancellation, payment.BookingId.ToString("N")),
            (PostgresAdvisoryLocks.Scope.PaymentRefund, payment.Id.ToString("N")));
        if (ownTransaction is not null)
        {
            // Unreachable: the property lock above already requires the caller's transaction.
            await ownTransaction.DisposeAsync();
            throw new InvalidOperationException("The payment settlement must run inside the webhook event transaction.");
        }

        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Bookings" WHERE "Id" = {payment.BookingId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }
}
