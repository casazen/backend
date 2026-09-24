using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Host cancellation of a booking with its money on Stripe (BK-02, #51, A3-05). See <see cref="IBookingCancellationService"/>.
/// </summary>
/// <remarks>
/// Under an advisory lock on the booking and on each of its payments (same order as the payment refunds, so no
/// deadlock): the refund decision is validated, the intents not paid yet are canceled on the connected account, the
/// booking is set Cancelled and the refunds are reserved, all in one transaction. The refunds are then sent to Stripe
/// with their idempotency keys; one that Stripe rejects stays visible as failed and can be retried from the payment.
/// Finally the guest gets the "booking cancelled" email (PC-07, A2-08); the host's reason is kept on the booking and
/// never sent to the guest.
/// </remarks>
public sealed class BookingCancellationService(
    AppDbContext db,
    PaymentRefundService refundService,
    IStripeService stripeService,
    IEmailQueue emailQueue,
    ILogger<BookingCancellationService> logger,
    TimeProvider? timeProvider = null) : IBookingCancellationService
{
    private const string Currency = "EUR";

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
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, locks.ToArray()))
        {
            booking = await LoadBookingAsync(request.BookingId, cancellationToken);
            if (booking.Status == BookingStatus.Cancelled)
                throw new DomainConflictException("booking_already_cancelled", "BookingAlreadyCancelled");
            if (!IsCancellable(booking.Status))
                throw new DomainRuleException("booking_not_cancellable", "BookingNotCancellable");

            var payments = await db.Payments.Where(p => p.BookingId == booking.Id).ToListAsync(cancellationToken);
            var refunds = await LoadRefundsAsync(payments, cancellationToken);
            var quote = BuildQuote(booking, payments, refunds);
            ValidateRefundDecision(quote, request.RefundAmount);

            canceledIntents = await CancelUncollectedIntentsAsync(booking, payments, cancellationToken);

            var now = _clock.GetUtcNow().UtcDateTime;
            booking.Status = BookingStatus.Cancelled;
            booking.CancellationNote = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            booking.CheckoutReminderJobId = null;
            booking.UpdatedAt = now;
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

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Booking {BookingId} cancelled: {RefundCount} refund(s) of {RefundAmount} EUR, {IntentCount} intent(s) canceled",
            booking.Id,
            reserved.Count,
            reserved.Sum(r => r.Amount),
            canceledIntents);

        var submitted = new List<PaymentRefund>();
        foreach (var refund in reserved)
            submitted.Add(await refundService.SubmitAsync(refund, throwOnTransientFailure: false, cancellationToken));

        await NotifyGuestAsync(booking, submitted, cancellationToken);
        return new BookingCancellationResult(booking, submitted, canceledIntents);
    }

    /// <summary>
    /// Queues the "booking cancelled" email to the guest (IT, like every guest email for now), with the refund started
    /// when there is one: its confirmation arrives separately, once Stripe reports it succeeded. The cancellation is
    /// already saved: a failure here is logged and never undoes it.
    /// </summary>
    private async Task NotifyGuestAsync(Booking booking, IReadOnlyList<PaymentRefund> refunds, CancellationToken cancellationToken)
    {
        try
        {
            var guest = await db.Guests.AsNoTracking()
                .Where(g => g.Id == booking.GuestId)
                .Select(g => new { g.FirstName, g.Email })
                .FirstOrDefaultAsync(cancellationToken);
            if (guest is null)
                return;

            var refundStarted = refunds
                .Where(r => r.Status is not (PaymentRefundStatus.Failed or PaymentRefundStatus.Canceled))
                .Sum(r => r.Amount);
            var email = EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                guest.FirstName,
                booking.Property?.Name ?? string.Empty,
                booking.CheckInDate,
                booking.CheckOutDate,
                refundStarted > 0 ? refundStarted : null);

            if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestBookingCancelled))
                logger.LogWarning("Booking {BookingId} cancelled but the guest email was not queued", booking.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Guest email of cancelled booking {BookingId} could not be queued", booking.Id);
        }
    }

    private static bool IsCancellable(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

    private BookingCancellationQuote BuildQuote(Booking booking, IReadOnlyList<Payment> payments, IReadOnlyList<PaymentRefund> refunds)
    {
        var stripePaid = StripePaidPayments(payments).ToList();
        var summaries = stripePaid
            .Select(p => PaymentRefundService.Summarize(p, refunds.Where(r => r.PaymentId == p.Id)))
            .ToList();

        var paid = summaries.Sum(s => s.PaidAmount);
        var refunded = summaries.Sum(s => s.RefundedAmount);
        var pending = summaries.Sum(s => s.PendingRefundAmount);
        var refundable = summaries.Sum(s => s.RefundableAmount);

        var floor = CancellationRefundPolicy.Evaluate(booking, booking.Property?.CancellationPolicy, _clock.GetUtcNow());
        var minimum = Math.Min(refundable, CancellationRefundPolicy.MinimumRefund(floor, paid, refunded + pending));

        var offlinePaid = payments
            .Where(p => PaymentRefundService.PaymentIntentIdOf(p) is null && p.Status == PaymentStatus.Completed)
            .Sum(p => p.Amount);

        return new BookingCancellationQuote(
            booking.Id,
            booking.Status,
            IsCancellable(booking.Status),
            Currency,
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
            throw new DomainRuleException(
                "booking_cancel_refund_required",
                "BookingCancelRefundRequired",
                quote.RefundableAmount);
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
