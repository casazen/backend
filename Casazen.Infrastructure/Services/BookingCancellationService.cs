using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Cancellation of a booking with its money on Stripe, by the host (BK-02, #51, A3-05) or by the guest through the signed
/// link (BK-07, DB-08h). See <see cref="IBookingCancellationService"/>.
/// </summary>
/// <remarks>
/// Under an advisory lock on the booking and on each of its payments (same order as the payment refunds, so no
/// deadlock): the booking and its payments are read again, the refund decision is validated, the intents not paid yet
/// are canceled on the connected account, the booking is set Cancelled (and the guest's link spent, when the guest
/// cancels) and the refunds are reserved, all in one transaction. The refunds are then sent to Stripe
/// with their idempotency keys; one that Stripe rejects stays visible as failed and can be retried from the payment.
/// Finally the guest gets one "booking cancelled" email (PC-07, A2-08) that includes the refund: as made when Stripe
/// confirmed it at once (its separate "refund confirmed" email is then not sent, BK-10), as started otherwise (the
/// confirmation follows). The host's reason is kept on the booking and never sent to the guest.
/// </remarks>
public sealed class BookingCancellationService(
    AppDbContext db,
    PaymentRefundService refundService,
    IStripeService stripeService,
    BookingNotifier notifier,
    ILogger<BookingCancellationService> logger,
    TimeProvider? timeProvider = null) : IBookingCancellationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<BookingCancellationQuote> GetQuoteAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var payments = await db.Payments.Where(p => p.BookingId == bookingId).ToListAsync(cancellationToken);
        var refunds = await LoadRefundsAsync(payments, cancellationToken);
        return BuildQuote(booking, payments, refunds);
    }

    public async Task<BookingCancellationResult> CancelAsync(
        BookingCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var paymentIds = await db.Payments
            .Where(p => p.BookingId == request.BookingId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var locks = new List<(PostgresAdvisoryLocks.Scope, string)>
        {
            (PostgresAdvisoryLocks.Scope.BookingCancellation, request.BookingId.ToString("N")),
        };
        locks.AddRange(paymentIds.Order().Select(id => (PostgresAdvisoryLocks.Scope.PaymentRefund, id.ToString("N"))));

        Booking booking;
        var reserved = new List<PaymentRefund>();
        int canceledIntents;
        var retained = 0m;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, locks.ToArray()))
        {
            // Whatever the caller read before this lock may be old by now: a click that was first has cancelled the booking
            // and refunded meanwhile. A query does not overwrite an entity the context already tracks, so the booking would
            // still look cancellable and a refund of only part of the payment (a guest under a 50% policy) would leave the
            // rest refundable for a second time. Read again, under the lock, from what is committed.
            db.ChangeTracker.Clear();

            booking = await LoadBookingAsync(request.BookingId, cancellationToken);
            if (booking.Status == BookingStatus.Cancelled)
                throw new DomainConflictException(BookingErrorCodes.AlreadyCancelled, "BookingAlreadyCancelled");
            if (!IsCancellable(booking.Status))
                throw new DomainRuleException("booking_not_cancellable", "BookingNotCancellable");

            var payments = await db.Payments.Where(p => p.BookingId == booking.Id).ToListAsync(cancellationToken);
            var refunds = await LoadRefundsAsync(payments, cancellationToken);
            var quote = BuildQuote(booking, payments, refunds, request.Initiator);
            ValidateRefundDecision(quote, request.RefundAmount);

            canceledIntents = await CancelUncollectedIntentsAsync(booking, payments, cancellationToken);

            var now = _clock.GetUtcNow().UtcDateTime;
            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAt = now;
            booking.CancellationNote = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            booking.UpdatedAt = now;

            // The host's timeline tells the guest's own cancellation apart from the host's (DB-08a).
            if (request.Initiator == BookingCancellationInitiator.Guest)
                booking.CancellationReason = BookingCancellationReason.GuestRequested;

            // The guest's cancel link is spent by the cancellation that succeeds, in the same transaction: an error above
            // leaves it usable until it expires. The hash stays, so a second click is recognised and answered with the
            // outcome (the cancelled booking is what stops a second refund, not the link).
            if (request.Initiator == BookingCancellationInitiator.Guest && booking.GuestCancelTokenExpiresAt > now)
                booking.GuestCancelTokenExpiresAt = now;

            await db.SaveChangesAsync(cancellationToken);

            var remaining = request.RefundAmount ?? 0m;
            foreach (var payment in StripePaidPayments(payments).OrderBy(p => p.CreatedAt))
            {
                if (remaining <= 0)
                    break;

                var available = PaymentRefundService
                    .Summarize(payment, refunds.Where(r => r.PaymentId == payment.Id))
                    .RefundableAmount;
                var part = Math.Min(available, remaining);
                if (part <= 0)
                    continue;

                reserved.Add(await refundService.ReserveAsync(
                    payment,
                    part,
                    PaymentRefundOrigin.BookingCancellation,
                    request.Reason,
                    request.RequestedByUserId,
                    cancellationToken));
                remaining -= part;
            }

            // What stays with the host of what could have been refunded: the host is told when the guest cancels (DB-08a).
            retained = Math.Max(0m, quote.RefundableAmount - reserved.Sum(r => r.Amount));

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Booking {BookingId} cancelled by the {Initiator}: {RefundCount} refund(s) of {RefundAmount} {Currency}, {IntentCount} intent(s) canceled",
            booking.Id,
            request.Initiator,
            reserved.Count,
            reserved.Sum(r => r.Amount),
            StayCurrency.ForProperty(booking.Property),
            canceledIntents);

        var submitted = new List<PaymentRefund>();
        foreach (var refund in reserved)
            submitted.Add(await refundService.SubmitAsync(refund, throwOnTransientFailure: false, cancellationToken, notifyGuest: false));

        await NotifyAsync(booking.Id, submitted, request.Initiator, retained, cancellationToken);
        return new BookingCancellationResult(booking, submitted, canceledIntents);
    }

    /// <summary>
    /// One "booking cancelled" email to the guest with the refund: what Stripe confirmed at once (whose own "refund
    /// confirmed" email is claimed here, so the guest does not get two) and what is still in progress (confirmed by a
    /// later email). When the guest cancelled, the email says so (it is not "cancelled by the host") and the host gets one too,
    /// with the refund and what stays with them (DB-08a). The cancellation is already saved: a failure here is logged and never
    /// undoes it.
    /// </summary>
    private async Task NotifyAsync(
        Guid bookingId,
        IReadOnlyList<PaymentRefund> refunds,
        BookingCancellationInitiator initiator,
        decimal retainedAmount,
        CancellationToken cancellationToken)
    {
        var refunded = 0m;
        var started = 0m;
        try
        {
            foreach (var refund in refunds)
            {
                if (refund.Status == PaymentRefundStatus.Succeeded)
                {
                    // Claimed by a webhook already: the guest also got "refund confirmed", the amount is still refunded.
                    await refundService.ClaimGuestNotificationAsync(refund.Id, cancellationToken);
                    refunded += refund.Amount;
                }
                else if (refund.Status is PaymentRefundStatus.Pending or PaymentRefundStatus.RequiresAction)
                {
                    started += refund.Amount;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Refund notifications of cancelled booking {BookingId} could not be claimed", bookingId);
        }

        if (initiator == BookingCancellationInitiator.Guest)
            await notifier.BookingCancelledByGuestAsync(bookingId, refunded, started, retainedAmount, cancellationToken);
        else
            await notifier.BookingCancelledByHostAsync(bookingId, refunded, started, cancellationToken);
    }

    private static bool IsCancellable(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

    private BookingCancellationQuote BuildQuote(
        Booking booking,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<PaymentRefund> refunds,
        BookingCancellationInitiator initiator = BookingCancellationInitiator.Host)
    {
        var stripePaid = StripePaidPayments(payments).ToList();
        var summaries = stripePaid
            .Select(p => PaymentRefundService.Summarize(p, refunds.Where(r => r.PaymentId == p.Id)))
            .ToList();

        var paid = summaries.Sum(s => s.PaidAmount);
        var refunded = summaries.Sum(s => s.RefundedAmount);
        var pending = summaries.Sum(s => s.PendingRefundAmount);
        var refundable = summaries.Sum(s => s.RefundableAmount);

        // BK-02, PO 2026-10-08: host cancellation always refunds 100% to the guest, regardless of the policy.
        // The floor evaluated from the model is kept for reference only (shown as the rule in the response).
        var now = _clock.GetUtcNow();
        var nights = (booking.CheckOutDate.Date - booking.CheckInDate.Date).Days;
        var policy = CancellationRefundPolicy.EffectivePolicy(booking.Property, nights);
        var floor = CancellationRefundPolicy.Evaluate(booking, policy, now);

        // BK-02, BK-07: a guest who cancels themselves gets what the property's policy grants, no more than what is
        // refundable (DB-08h: asking them for everything refundable, as for the host, refused every cancellation that
        // the policy does not refund in full).
        var minimum = initiator == BookingCancellationInitiator.Guest
            ? CancellationRefundPolicy.EvaluateGuestRefund(booking, now, refundable).Amount
            : refundable;

        var offlinePaid = payments
            .Where(p => PaymentRefundService.PaymentIntentIdOf(p) is null && p.Status == PaymentStatus.Completed)
            .Sum(p => p.Amount);

        return new BookingCancellationQuote(
            booking.Id,
            booking.Status,
            IsCancellable(booking.Status),
            // The refund is in the currency of the charge, which is the one of the property (CHF-MONEY): Stripe refunds in it.
            StayCurrency.ForProperty(booking.Property),
            paid,
            refunded,
            pending,
            refundable,
            minimum,
            floor.Rule,
            booking.FreeRefundDeadline?.Date,
            booking.Property?.CancellationPolicy?.Name,
            offlinePaid,
            payments.Any(IsUncollectedIntent));
    }

    private static void ValidateRefundDecision(BookingCancellationQuote quote, decimal? refundAmount)
    {
        if (quote.RefundableAmount <= 0)
        {
            if (refundAmount is > 0)
                throw new DomainRuleException("booking_cancel_nothing_to_refund", "BookingCancelNothingToRefund");
            return;
        }

        if (refundAmount is not { } amount)
        {
            // A host always owes the whole refundable amount, so a missing amount is refused; a guest whose policy
            // refunds nothing owes nothing, and no amount means no refund.
            if (quote.MinimumRefundAmount > 0)
            {
                throw new DomainRuleException(
                    "booking_cancel_refund_required",
                    "BookingCancelRefundRequired",
                    quote.RefundableAmount);
            }

            return;
        }

        if (amount < 0 || decimal.Round(amount, 2) != amount)
            throw new DomainRuleException("refund_amount_invalid", "RefundAmountInvalid");

        if (amount > quote.RefundableAmount)
        {
            throw new DomainRuleException(
                "refund_amount_exceeds_refundable",
                "RefundAmountExceedsRefundable",
                quote.RefundableAmount);
        }

        if (amount < quote.MinimumRefundAmount)
        {
            throw new DomainRuleException(
                "booking_cancel_refund_below_minimum",
                "BookingCancelRefundBelowMinimum",
                quote.MinimumRefundAmount);
        }
    }

    /// <summary>
    /// Cancels on Stripe what the guest has not paid yet: the PaymentIntent of an immediate payment, the SetupIntent
    /// (or the saved card) of a deferred one, and a PaymentIntent whose attempt failed but stays payable (declined
    /// checkout card, failed deferred charge waiting for the guest, BK-08). A PaymentIntent that is succeeding right now stops the cancellation
    /// (409): once its webhook has recorded the payment, the host cancels again with a refund. Payments without
    /// Stripe (cash on site, recorded by the host) that were still pending are canceled too.
    /// </summary>
    private async Task<int> CancelUncollectedIntentsAsync(
        Booking booking,
        IReadOnlyList<Payment> payments,
        CancellationToken cancellationToken)
    {
        var canceled = 0;
        foreach (var payment in payments.Where(IsUncollectedIntentOrPending))
        {
            var paymentIntentId = PaymentRefundService.PaymentIntentIdOf(payment);
            if (paymentIntentId is not null)
            {
                var account = await RequireAccountAsync(payment, cancellationToken);
                var intent = await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
                if (intent.Status is "succeeded" or "processing")
                    throw new DomainConflictException("booking_payment_in_progress", "BookingPaymentInProgress");

                if (intent.Status != "canceled")
                {
                    await stripeService.CancelPaymentIntentAsync(
                        paymentIntentId,
                        account,
                        $"booking-cancel:{booking.Id:N}:{paymentIntentId}",
                        cancellationToken);
                }

                canceled++;
            }
            else if (payment.TransactionId.StartsWith("seti_", StringComparison.Ordinal))
            {
                var account = await RequireAccountAsync(payment, cancellationToken);
                var setupIntent = await stripeService.GetSetupIntentAsync(payment.TransactionId, account, cancellationToken);
                switch (setupIntent.Status)
                {
                    case "requires_payment_method" or "requires_confirmation" or "requires_action":
                        await stripeService.CancelSetupIntentAsync(
                            payment.TransactionId,
                            account,
                            $"booking-cancel:{booking.Id:N}:{payment.TransactionId}",
                            cancellationToken);
                        break;
                    case "processing":
                        throw new DomainConflictException("booking_payment_in_progress", "BookingPaymentInProgress");
                    case "succeeded":
                        // A succeeded SetupIntent cannot be canceled: the saved card is detached instead, so the
                        // deadline charge can never happen (the job also skips cancelled bookings).
                        await DetachSavedCardAsync(booking, setupIntent.PaymentMethodId, account, cancellationToken);
                        break;
                }

                canceled++;
            }

            payment.Status = PaymentStatus.Canceled;
            payment.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }

        return canceled;
    }

    private async Task DetachSavedCardAsync(
        Booking booking,
        string? paymentMethodId,
        string account,
        CancellationToken cancellationToken)
    {
        var id = booking.StripePaymentMethodId ?? paymentMethodId;
        if (string.IsNullOrWhiteSpace(id))
            return;

        try
        {
            await stripeService.DetachPaymentMethodAsync(id, account, cancellationToken);
        }
        catch (Stripe.StripeException ex)
        {
            // Not blocking: the booking is cancelled and the deadline job never charges a cancelled booking.
            logger.LogWarning(ex, "Saved card of cancelled booking {BookingId} could not be detached", booking.Id);
        }
    }

    private async Task<string> RequireAccountAsync(Payment payment, CancellationToken cancellationToken) =>
        await refundService.ResolveAccountAsync(payment, cancellationToken)
        ?? throw new DomainRuleException("payment_refund_account_missing", "PaymentRefundAccountMissing");

    private static IEnumerable<Payment> StripePaidPayments(IEnumerable<Payment> payments) =>
        payments.Where(p =>
            p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded &&
            PaymentRefundService.PaymentIntentIdOf(p) is not null);

    private static bool IsUncollectedIntent(Payment payment) =>
        (payment.Status == PaymentStatus.Pending &&
         (PaymentRefundService.PaymentIntentIdOf(payment) is not null ||
          payment.TransactionId.StartsWith("seti_", StringComparison.Ordinal))) ||
        (payment.Status == PaymentStatus.Failed && PaymentRefundService.PaymentIntentIdOf(payment) is not null);

    /// <summary>Pending payments (with or without Stripe) and failed PaymentIntents that can still be paid.</summary>
    private static bool IsUncollectedIntentOrPending(Payment payment) =>
        payment.Status == PaymentStatus.Pending ||
        (payment.Status == PaymentStatus.Failed && PaymentRefundService.PaymentIntentIdOf(payment) is not null);

    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Property)
            .ThenInclude(p => p.CancellationPolicy)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private async Task<List<PaymentRefund>> LoadRefundsAsync(IReadOnlyList<Payment> payments, CancellationToken cancellationToken)
    {
        var ids = payments.Select(p => p.Id).ToList();
        return ids.Count == 0
            ? []
            : await db.PaymentRefunds.Where(r => ids.Contains(r.PaymentId)).ToListAsync(cancellationToken);
    }
}
