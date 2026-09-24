using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>What one run of the deferred charge job did, booking by booking.</summary>
public sealed record DeferredChargeRun(int Succeeded, int Processing, int AwaitingGuest, int Errors, int Cancelled, int Skipped)
{
    public static DeferredChargeRun Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>What the payment webhook did with an event of a deferred charge PaymentIntent.</summary>
public enum DeferredChargeWebhookOutcome
{
    /// <summary>No booking for the PaymentIntent, event from another account, or nothing to change.</summary>
    Ignored,

    /// <summary>The deferred payment was updated (completed, processing, failed, canceled).</summary>
    Applied,

    /// <summary>
    /// The PaymentIntent succeeded on a booking that is no longer confirmed: the caller settles it like any booking
    /// payment (<see cref="CheckoutPaymentSettlementService"/>, BK-04: confirmed again or refunded in full).
    /// </summary>
    SettleAsCheckoutPayment,
}

/// <summary>Result of <see cref="DeferredChargeService.ApplyPaymentIntentEventAsync"/>; pass it to <see cref="DeferredChargeService.CompleteAsync"/> after the commit.</summary>
public sealed record DeferredChargeWebhookResult(DeferredChargeWebhookOutcome Outcome, DeferredChargeNotice? Notice = null)
{
    public static DeferredChargeWebhookResult Ignored { get; } = new(DeferredChargeWebhookOutcome.Ignored);
}

/// <summary>Emails due once a change of a deferred charge is committed.</summary>
public enum DeferredChargeNoticeKind
{
    /// <summary>The charge failed and needs the guest: guest (link to pay) and host.</summary>
    GuestMustPay,

    /// <summary>Every attempt ended without an answer from Stripe (or without a saved card): host only.</summary>
    ChargeNotAttempted,

    /// <summary>Not paid in time: booking cancelled, guest and host.</summary>
    Cancelled,
}

/// <param name="CheckoutToken">Raw checkout token of the link to pay (<see cref="DeferredChargeNoticeKind.GuestMustPay"/>); only its hash is stored.</param>
public sealed record DeferredChargeNotice(Guid BookingId, DeferredChargeNoticeKind Kind, string? CheckoutToken = null);

/// <summary>
/// The deferred payment of the public checkout, "Paga alla scadenza" (task BK-08, audit defect A3-14): off-session charge
/// of the saved payment method on the host's connected account, payment state from the PaymentIntent status and the
/// webhooks, guest and host emails on failure, limited attempts, automatic cancellation. Rules and settings:
/// <see cref="DeferredCharges"/>; runbooks <c>docs/runbooks/direct-booking.md</c> § 8 and <c>stripe.md</c> "Deferred charge".
/// </summary>
/// <remarks>
/// <para>
/// Each booking is handled in its own READ COMMITTED transaction under the BK-02 booking-cancellation and payment-refund
/// advisory locks, held for the Stripe calls: two runs of the job, the payment webhook and a host cancellation of the
/// same booking take turns, and a run that waited reads the booking again (attempt of the day already made: skipped).
/// The attempt is recorded in the same transaction as its result; a crash before the commit leaves no attempt, and the
/// retry sends the same idempotency key (same PaymentIntent). Before creating a PaymentIntent the customer's
/// PaymentIntents are read on the connected account, so an attempt whose answer was lost never creates a second charge.
/// </para>
/// <para>Emails are queued after the commit (FD-13); logs carry booking, payment and Stripe ids only.</para>
/// </remarks>
public sealed class DeferredChargeService(
    AppDbContext db,
    IStripeService stripeService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<DeferredChargeService> logger,
    TimeProvider? timeProvider = null)
{
    private const string Currency = "eur";
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";

    /// <summary>PaymentIntent statuses the guest (or a new off-session attempt) can still pay.</summary>
    private static readonly HashSet<string> PayableStatuses = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private enum Step
    {
        Succeeded,
        Processing,
        AwaitingGuest,
        Error,
        Cancelled,
        Skipped,
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One run of the <c>direct-booking-charge</c> job: every confirmed "Paga alla scadenza" booking whose deadline has
    /// come and whose payment was not collected is charged (at most once per Rome day), synchronized with Stripe while in
    /// flight, or cancelled when unpaid past its cancellation day.
    /// </summary>
    public async Task<DeferredChargeRun> RunDueChargesAsync(CancellationToken cancellationToken = default)
    {
        var today = _clock.TodayInRome();
        var maxAttempts = DeferredCharges.GetMaxAttempts(configuration);

        var bookingIds = await db.Bookings
            .AsNoTracking()
            .Where(b => b.PaymentOption == PaymentOption.OnCancellationDeadline &&
                        b.FreeRefundDeadline != null &&
                        b.FreeRefundDeadline <= today &&
                        (b.Status == BookingStatus.Confirmed ||
                         b.Status == BookingStatus.CheckedIn ||
                         b.Status == BookingStatus.CheckedOut))
            // Collected (possibly refunded since) or canceled: never charged again.
            .Where(b => !db.Payments.Any(p =>
                p.BookingId == b.Id &&
                (p.Description == DeferredCharges.PaymentDescription ||
                 p.Description == DeferredCharges.LegacyPaymentDescription ||
                 p.TransactionId == b.StripeSetupIntentId) &&
                (p.Status == PaymentStatus.Completed ||
                 p.Status == PaymentStatus.Refunded ||
                 p.Status == PaymentStatus.PartiallyRefunded ||
                 p.Status == PaymentStatus.Canceled)))
            // Something left to do: an attempt, the cancellation of an unpaid booking, or a payment in flight to check.
            .Where(b => b.DeferredChargeAttempts < maxAttempts ||
                        (b.DeferredChargeFailedAt != null && b.Status == BookingStatus.Confirmed && b.CheckInDate > today) ||
                        db.Payments.Any(p => p.BookingId == b.Id && p.Status == PaymentStatus.Processing))
            .OrderBy(b => b.FreeRefundDeadline)
            .ThenBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        if (bookingIds.Count == 0)
            return DeferredChargeRun.Empty;

        int succeeded = 0, processing = 0, awaitingGuest = 0, errors = 0, cancelled = 0, skipped = 0;
        foreach (var bookingId in bookingIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (step, notice) = await ProcessBookingAsync(bookingId, today, maxAttempts, cancellationToken);
            if (notice is not null)
                await CompleteAsync(notice, cancellationToken);

            switch (step)
            {
                case Step.Succeeded: succeeded++; break;
                case Step.Processing: processing++; break;
                case Step.AwaitingGuest: awaitingGuest++; break;
                case Step.Error: errors++; break;
                case Step.Cancelled: cancelled++; break;
                default: skipped++; break;
            }
        }

        return new DeferredChargeRun(succeeded, processing, awaitingGuest, errors, cancelled, skipped);
    }

    private async Task<(Step Step, DeferredChargeNotice? Notice)> ProcessBookingAsync(
        Guid bookingId,
        DateTime today,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        // One booking at a time in this context: nothing tracked from the previous one is saved with this one.
        db.ChangeTracker.Clear();
        try
        {
            var paymentIds = await db.Payments
                .Where(p => p.BookingId == bookingId)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
            var locks = new List<(PostgresAdvisoryLocks.Scope, string)>
            {
                (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")),
            };
            locks.AddRange(paymentIds.Order().Select(id => (PostgresAdvisoryLocks.Scope.PaymentRefund, id.ToString("N"))));

            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, locks.ToArray());

            // Read again under the locks: another run, the webhook or the host may have changed it meanwhile.
            var booking = await db.Bookings
                .Include(b => b.Payments)
                .Include(b => b.Org)
                .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
            if (booking is null || !IsDue(booking, today))
                return (Step.Skipped, null);

            var payment = DeferredCharges.FindPayment(booking, booking.Payments);
            if (payment is not null && (DeferredCharges.IsCollected(payment.Status) || payment.Status == PaymentStatus.Canceled))
                return (Step.Skipped, null);

            var account = payment?.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
            (Step Step, DeferredChargeNotice? Notice) result;
            if (payment?.Status == PaymentStatus.Processing)
                result = await SynchronizeInFlightAsync(booking, payment, account, cancellationToken);
            else if (IsCancellationDue(booking, today))
                result = await CancelUnpaidAsync(booking, payment, account, cancellationToken);
            else if (booking.DeferredChargeAttempts < maxAttempts && booking.DeferredChargeLastAttemptOn != today)
                result = await AttemptAsync(booking, payment, account, today, maxAttempts, cancellationToken);
            else
                return (Step.Skipped, null);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Deferred charge of booking {BookingId} could not be processed; the next run retries it", bookingId);
            db.ChangeTracker.Clear();
            return (Step.Error, null);
        }
    }

    private static bool IsDue(Booking booking, DateTime today) =>
        booking.PaymentOption == PaymentOption.OnCancellationDeadline &&
        booking.FreeRefundDeadline is { } deadline &&
        deadline <= today &&
        booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut;

    private bool IsCancellationDue(Booking booking, DateTime today) =>
        booking.Status == BookingStatus.Confirmed &&
        DeferredCharges.CancellationDay(booking, DeferredCharges.GetCancelAfterDays(configuration)) is { } day &&
        RomeCalendar.DateInRome(today) >= day;

    /// <summary>One off-session attempt, recorded with its result in the booking's transaction.</summary>
    private async Task<(Step, DeferredChargeNotice?)> AttemptAsync(
        Booking booking,
        Payment? payment,
        string? account,
        DateTime today,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var attempt = booking.DeferredChargeAttempts + 1;
        booking.DeferredChargeAttempts = attempt;
        booking.DeferredChargeLastAttemptOn = today;
        booking.UpdatedAt = UtcNow;

        PaymentIntent? paymentIntent = null;
        string? failure = null;
        if (string.IsNullOrWhiteSpace(account) ||
            string.IsNullOrWhiteSpace(booking.StripeCustomerId) ||
            string.IsNullOrWhiteSpace(booking.StripePaymentMethodId))
        {
            failure = "no connected account or saved payment method";
        }
        else
        {
            try
            {
                paymentIntent = await ChargeAsync(booking, payment, account, attempt, cancellationToken);
            }
            catch (StripeException ex) when (ex.StripeError?.PaymentIntent is { } failed)
            {
                // 402: the payment needs the guest (authentication_required, card_declined…); the PaymentIntent stays
                // payable (requires_payment_method) for the link of the email.
                paymentIntent = failed;
                logger.LogWarning(
                    "Deferred charge of booking {BookingId} failed (attempt {Attempt}): {Code} {DeclineCode}",
                    booking.Id,
                    attempt,
                    ex.StripeError.Code,
                    ex.StripeError.DeclineCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // No PaymentIntent in the answer (network, 5xx, rate limit, invalid request): nothing to show the guest.
                failure = ex is StripeException stripeError ? $"Stripe {stripeError.StripeError?.Code ?? stripeError.HttpStatusCode.ToString()}" : ex.GetType().Name;
                logger.LogError(ex, "Deferred charge of booking {BookingId} not attempted on Stripe (attempt {Attempt})", booking.Id, attempt);
            }
        }

        if (paymentIntent is null)
        {
            logger.LogWarning(
                "Deferred charge of booking {BookingId}: attempt {Attempt} of {MaxAttempts} without payment ({Failure})",
                booking.Id,
                attempt,
                maxAttempts,
                failure);
            // Every attempt used and the guest was never asked: the host handles the payment.
            return attempt >= maxAttempts && booking.DeferredChargeFailedAt is null
                ? (Step.Error, new DeferredChargeNotice(booking.Id, DeferredChargeNoticeKind.ChargeNotAttempted))
                : (Step.Error, null);
        }

        var notice = ApplyPaymentIntent(booking, payment, paymentIntent, account);
        logger.LogInformation(
            "Deferred charge of booking {BookingId}: attempt {Attempt}, payment intent {PaymentIntentId} {Status}",
            booking.Id,
            attempt,
            paymentIntent.Id,
            paymentIntent.Status);
        return (StepOf(paymentIntent.Status), notice);
    }

    /// <summary>
    /// The PaymentIntent of this attempt: the booking's own one confirmed again when the previous attempt failed, one of
    /// a lost answer adopted, or a new one (off-session, idempotency key of booking, deadline and attempt).
    /// </summary>
    private async Task<PaymentIntent> ChargeAsync(
        Booking booking,
        Payment? payment,
        string account,
        int attempt,
        CancellationToken cancellationToken)
    {
        var existing = payment is not null && PaymentRefundService.PaymentIntentIdOf(payment) is { } paymentIntentId
            ? await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken)
            : await FindLostPaymentIntentAsync(booking, account, cancellationToken);

        if (existing is null)
        {
            return await stripeService.ChargePaymentMethodAsync(
                account,
                booking.StripeCustomerId!,
                booking.StripePaymentMethodId!,
                ToCents(booking.TotalPrice),
                Currency,
                new Dictionary<string, string>
                {
                    ["bookingId"] = booking.Id.ToString(),
                    ["propertyId"] = booking.PropertyId.ToString(),
                    ["orgId"] = booking.OrgId.ToString(),
                    ["kind"] = DeferredCharges.Kind,
                },
                DeferredCharges.CreationIdempotencyKey(booking.Id, booking.FreeRefundDeadline!.Value, attempt),
                cancellationToken);
        }

        if (existing.Status is not null && PayableStatuses.Contains(existing.Status))
        {
            return await stripeService.ConfirmPaymentIntentOffSessionAsync(
                existing.Id,
                account,
                booking.StripePaymentMethodId!,
                DeferredCharges.ConfirmationIdempotencyKey(existing.Id, attempt),
                cancellationToken);
        }

        // Succeeded or processing (the guest paid from the link, the webhook is late), or canceled on Stripe (outside
        // CasaZen: no new charge).
        return existing;
    }

    /// <summary>A deferred charge PaymentIntent of this booking on the customer, not canceled: an attempt whose answer was lost.</summary>
    private async Task<PaymentIntent?> FindLostPaymentIntentAsync(Booking booking, string account, CancellationToken cancellationToken)
    {
        var bookingId = booking.Id.ToString();
        var intents = await stripeService.ListCustomerPaymentIntentsAsync(booking.StripeCustomerId!, account, cancellationToken);
        var lost = intents.FirstOrDefault(pi =>
            pi.Status != "canceled" &&
            pi.Metadata is not null &&
            pi.Metadata.TryGetValue("kind", out var kind) && kind == DeferredCharges.Kind &&
            pi.Metadata.TryGetValue("bookingId", out var id) && id == bookingId);
        if (lost is not null)
        {
            logger.LogWarning(
                "Deferred charge of booking {BookingId}: payment intent {PaymentIntentId} of an earlier attempt found on Stripe ({Status})",
                booking.Id,
                lost.Id,
                lost.Status);
        }

        return lost;
    }

    /// <summary>A payment Stripe reported in flight (e.g. SEPA): read it again, in case its webhook was lost. Not an attempt.</summary>
    private async Task<(Step, DeferredChargeNotice?)> SynchronizeInFlightAsync(
        Booking booking,
        Payment payment,
        string? account,
        CancellationToken cancellationToken)
    {
        if (PaymentRefundService.PaymentIntentIdOf(payment) is not { } paymentIntentId || string.IsNullOrWhiteSpace(account))
            return (Step.Skipped, null);

        var paymentIntent = await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
        if (DeferredCharges.StatusOf(paymentIntent.Status) == PaymentStatus.Processing)
            return (Step.Skipped, null);

        var notice = ApplyPaymentIntent(booking, payment, paymentIntent, account);
        return (StepOf(paymentIntent.Status), notice);
    }

    /// <summary>
    /// Unpaid on its cancellation day: a last look at Stripe (a payment made meanwhile wins), then the PaymentIntent is
    /// canceled and the saved payment method detached on the connected account, the booking cancelled and its dates released.
    /// </summary>
    private async Task<(Step, DeferredChargeNotice?)> CancelUnpaidAsync(
        Booking booking,
        Payment? payment,
        string? account,
        CancellationToken cancellationToken)
    {
        if (payment is not null && PaymentRefundService.PaymentIntentIdOf(payment) is { } paymentIntentId && !string.IsNullOrWhiteSpace(account))
        {
            var current = await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
            if (current.Status != "canceled")
            {
                if (current.Status is null || !PayableStatuses.Contains(current.Status))
                    return (StepOf(current.Status), ApplyPaymentIntent(booking, payment, current, account));

                try
                {
                    current = await stripeService.CancelPaymentIntentAsync(
                        paymentIntentId, account, $"deferred-charge-cancel:{booking.Id:N}:{paymentIntentId}", cancellationToken);
                }
                catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
                {
                    // The guest paid between the read and the cancellation.
                    current = await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
                }

                if (current.Status != "canceled")
                    return (StepOf(current.Status), ApplyPaymentIntent(booking, payment, current, account));
            }
        }

        if (!string.IsNullOrWhiteSpace(booking.StripePaymentMethodId) && !string.IsNullOrWhiteSpace(account))
        {
            try
            {
                await stripeService.DetachPaymentMethodAsync(booking.StripePaymentMethodId, account, cancellationToken);
            }
            catch (StripeException ex)
            {
                // Not blocking: the job never charges a cancelled booking.
                logger.LogWarning(ex, "Saved payment method of booking {BookingId} could not be detached", booking.Id);
            }
        }

        var now = UtcNow;
        if (payment is not null)
        {
            payment.Status = PaymentStatus.Canceled;
            payment.UpdatedAt = now;
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancellationReason = BookingCancellationReason.DeferredPaymentNotCompleted;
        booking.CheckoutReminderJobId = null;
        booking.UpdatedAt = now;
        logger.LogInformation(
            "Booking {BookingId} cancelled: deferred payment not completed since {FailedAt}; dates released",
            booking.Id,
            booking.DeferredChargeFailedAt);
        return (Step.Cancelled, new DeferredChargeNotice(booking.Id, DeferredChargeNoticeKind.Cancelled));
    }

    /// <summary>
    /// Applies an event of a deferred charge PaymentIntent (<c>metadata.kind = direct-booking-deadline-charge</c>) from the
    /// Connect endpoint or the platform endpoint (with the event's <c>account</c>), inside the event transaction (PL-10).
    /// Call <see cref="CompleteAsync"/> with the result's notice once the event is committed.
    /// </summary>
    public async Task<DeferredChargeWebhookResult> ApplyPaymentIntentEventAsync(
        PaymentIntent paymentIntent,
        string eventType,
        WebhookSource source,
        string? eventAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentIntent);
        if (source == WebhookSource.Platform && string.IsNullOrWhiteSpace(eventAccountId))
        {
            // Deferred charges are always created on the connected account: an event of the platform account is not one.
            logger.LogWarning(
                "Deferred charge event {EventType} for {PaymentIntentId} from the platform account: ignored", eventType, paymentIntent.Id);
            return DeferredChargeWebhookResult.Ignored;
        }

        var bookingId = await ResolveBookingIdAsync(paymentIntent, cancellationToken);
        if (bookingId is null)
        {
            logger.LogWarning("No booking for deferred charge payment intent {PaymentIntentId}: ignored", paymentIntent.Id);
            return DeferredChargeWebhookResult.Ignored;
        }

        var paymentIds = await db.Payments.Where(p => p.BookingId == bookingId).Select(p => p.Id).ToListAsync(cancellationToken);
        var locks = new List<(PostgresAdvisoryLocks.Scope, string)>
        {
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.Value.ToString("N")),
        };
        locks.AddRange(paymentIds.Order().Select(id => (PostgresAdvisoryLocks.Scope.PaymentRefund, id.ToString("N"))));
        var ownTransaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, locks.ToArray());
        if (ownTransaction is not null)
        {
            await ownTransaction.DisposeAsync();
            throw new InvalidOperationException("Deferred charge events must be applied inside the webhook event transaction.");
        }

        var booking = await db.Bookings
            .Include(b => b.Payments)
            .SingleAsync(b => b.Id == bookingId, cancellationToken);
        // The job may have written this booking in its own transaction while this one waited on the locks.
        await db.Entry(booking).ReloadAsync(cancellationToken);
        foreach (var row in booking.Payments)
            await db.Entry(row).ReloadAsync(cancellationToken);

        var payment = booking.Payments.FirstOrDefault(p => PaymentRefundService.PaymentIntentIdOf(p) == paymentIntent.Id)
                      ?? DeferredCharges.FindPayment(booking, booking.Payments);
        if (payment?.StripeAccountId is { } expected &&
            !string.IsNullOrWhiteSpace(eventAccountId) &&
            !string.Equals(expected, eventAccountId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Deferred charge payment intent {PaymentIntentId} reported by account {EventAccount}, expected {ExpectedAccount}: ignored",
                paymentIntent.Id,
                eventAccountId,
                expected);
            return DeferredChargeWebhookResult.Ignored;
        }

        var confirmed = booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut;
        var status = eventType switch
        {
            "payment_intent.succeeded" => PaymentStatus.Completed,
            "payment_intent.processing" => PaymentStatus.Processing,
            "payment_intent.canceled" => PaymentStatus.Canceled,
            _ => PaymentStatus.Failed,
        };

        if (status == PaymentStatus.Completed && !confirmed)
        {
            // Never "Completed" on a cancelled booking (BK-04): the settlement confirms it again or refunds it. It finds
            // the payment by its PaymentIntent.
            payment = EnsurePaymentRow(booking, payment, paymentIntent, eventAccountId);
            payment.TransactionId = paymentIntent.Id;
            payment.StripePaymentIntentId = paymentIntent.Id;
            if (!string.IsNullOrWhiteSpace(eventAccountId))
                payment.StripeAccountId ??= eventAccountId;
            payment.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Deferred charge {PaymentIntentId} succeeded on booking {BookingId} in status {Status}: settled as a late payment",
                paymentIntent.Id,
                booking.Id,
                booking.Status);
            return new DeferredChargeWebhookResult(DeferredChargeWebhookOutcome.SettleAsCheckoutPayment);
        }

        if (payment is not null && DeferredCharges.IsCollected(payment.Status))
        {
            if (status == PaymentStatus.Completed &&
                !string.Equals(PaymentRefundService.PaymentIntentIdOf(payment), paymentIntent.Id, StringComparison.Ordinal))
            {
                // A second deferred charge succeeded: recorded as its own payment so the host can refund it.
                logger.LogError(
                    "Booking {BookingId} has a second succeeded deferred charge {PaymentIntentId} besides payment {PaymentId}: refund one of them",
                    booking.Id,
                    paymentIntent.Id,
                    payment.Id);
                ApplyStatus(booking, EnsurePaymentRow(booking, null, paymentIntent, eventAccountId), paymentIntent, eventAccountId, PaymentStatus.Completed);
                await db.SaveChangesAsync(cancellationToken);
                return new DeferredChargeWebhookResult(DeferredChargeWebhookOutcome.Applied);
            }

            return DeferredChargeWebhookResult.Ignored;
        }

        if (payment?.Status == PaymentStatus.Canceled && status != PaymentStatus.Completed)
            return DeferredChargeWebhookResult.Ignored;

        payment = EnsurePaymentRow(booking, payment, paymentIntent, eventAccountId);
        var notice = ApplyStatus(booking, payment, paymentIntent, eventAccountId, status);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Deferred charge {PaymentIntentId} of booking {BookingId}: {EventType} applied (payment {Status})",
            paymentIntent.Id,
            booking.Id,
            eventType,
            payment.Status);
        return new DeferredChargeWebhookResult(DeferredChargeWebhookOutcome.Applied, notice);
    }

    private async Task<Guid?> ResolveBookingIdAsync(PaymentIntent paymentIntent, CancellationToken cancellationToken)
    {
        var byPayment = await db.Payments
            .Where(p => p.TransactionId == paymentIntent.Id || p.StripePaymentIntentId == paymentIntent.Id)
            .Select(p => (Guid?)p.BookingId)
            .FirstOrDefaultAsync(cancellationToken);
        if (byPayment is not null)
            return byPayment;

        if (paymentIntent.Metadata is not null &&
            paymentIntent.Metadata.TryGetValue("bookingId", out var raw) &&
            Guid.TryParse(raw, out var bookingId) &&
            await db.Bookings.AnyAsync(b => b.Id == bookingId, cancellationToken))
            return bookingId;

        return null;
    }

    /// <summary>Records <paramref name="paymentIntent"/>'s status on the deferred payment (created when missing).</summary>
    private DeferredChargeNotice? ApplyPaymentIntent(Booking booking, Payment? payment, PaymentIntent paymentIntent, string? account)
    {
        payment = EnsurePaymentRow(booking, payment, paymentIntent, account);
        return ApplyStatus(booking, payment, paymentIntent, account, DeferredCharges.StatusOf(paymentIntent.Status));
    }

    /// <summary>
    /// Sets the payment status; the first failure of a confirmed booking starts the "guest must pay" episode: a new
    /// checkout token for the link of the email (only its hash is stored) and the notice for the emails.
    /// </summary>
    private DeferredChargeNotice? ApplyStatus(
        Booking booking,
        Payment payment,
        PaymentIntent paymentIntent,
        string? account,
        PaymentStatus status)
    {
        var now = UtcNow;
        payment.TransactionId = paymentIntent.Id;
        payment.StripePaymentIntentId = paymentIntent.Id;
        if (!string.IsNullOrWhiteSpace(account))
            payment.StripeAccountId ??= account;
        payment.UpdatedAt = now;

        switch (status)
        {
            case PaymentStatus.Completed:
                payment.Status = PaymentStatus.Completed;
                payment.Amount = paymentIntent.Amount > 0 ? paymentIntent.Amount / 100m : payment.Amount;
                payment.ProcessedAt ??= now;
                booking.DeferredChargeFailedAt = null;
                booking.UpdatedAt = now;
                return null;

            case PaymentStatus.Processing:
                payment.Status = PaymentStatus.Processing;
                return null;

            case PaymentStatus.Canceled:
                payment.Status = PaymentStatus.Canceled;
                if (booking.Status != BookingStatus.Cancelled)
                {
                    logger.LogWarning(
                        "Deferred charge {PaymentIntentId} of booking {BookingId} was canceled on Stripe: no further attempt",
                        paymentIntent.Id,
                        booking.Id);
                }

                return null;

            default:
                payment.Status = PaymentStatus.Failed;
                if (booking.DeferredChargeFailedAt is not null ||
                    booking.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
                    return null;

                var token = CheckoutOutcomes.NewToken();
                booking.DeferredChargeFailedAt = now;
                booking.CheckoutTokenHash = CheckoutOutcomes.HashToken(token);
                booking.UpdatedAt = now;
                return new DeferredChargeNotice(booking.Id, DeferredChargeNoticeKind.GuestMustPay, token);
        }
    }

    private Payment EnsurePaymentRow(Booking booking, Payment? payment, PaymentIntent paymentIntent, string? account)
    {
        if (payment is not null)
            return payment;

        var now = UtcNow;
        payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = paymentIntent.Amount > 0 ? paymentIntent.Amount / 100m : booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Core.Entities.PaymentMethod.CreditCard,
            TransactionId = paymentIntent.Id,
            StripePaymentIntentId = paymentIntent.Id,
            StripeAccountId = string.IsNullOrWhiteSpace(account) ? null : account,
            Description = DeferredCharges.PaymentDescription,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Payments.Add(payment);
        booking.Payments.Add(payment);
        return payment;
    }

    private static Step StepOf(string? paymentIntentStatus) => DeferredCharges.StatusOf(paymentIntentStatus) switch
    {
        PaymentStatus.Completed => Step.Succeeded,
        PaymentStatus.Processing => Step.Processing,
        PaymentStatus.Canceled => Step.Skipped,
        _ => Step.AwaitingGuest,
    };

    private static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Queues the emails of a committed change: guest (link to pay, or cancellation) and host (<c>Org.ContactEmail</c>).
    /// A failure is logged with the booking id only and never undoes the change.
    /// </summary>
    public async Task CompleteAsync(DeferredChargeNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        try
        {
            var data = await db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == notice.BookingId)
                .Select(b => new NoticeData(
                    b.Id,
                    b.Guest.FirstName,
                    b.Guest.LastName,
                    b.Guest.Email,
                    b.Property.Name,
                    b.Org.Slug,
                    b.Org.ContactEmail,
                    b.CheckInDate,
                    b.CheckOutDate,
                    b.TotalPrice,
                    b.DeferredChargeFailedAt))
                .SingleOrDefaultAsync(cancellationToken);
            if (data is null)
            {
                logger.LogWarning("Deferred charge emails of booking {BookingId} skipped: booking not found", notice.BookingId);
                return;
            }

            var culture = EmailTemplates.DefaultCulture;
            var guestFullName = $"{data.GuestFirstName} {data.GuestLastName}".Trim();
            var hostBookingUrl = links.HostBooking(data.BookingId);
            switch (notice.Kind)
            {
                case DeferredChargeNoticeKind.GuestMustPay:
                    {
                        var cancelOn = DeferredCharges.CancellationDay(
                            data.FailedAt, data.CheckInDate, DeferredCharges.GetCancelAfterDays(configuration));
                        DateTime? cancelOnDate = cancelOn?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                        Queue(data.BookingId, EmailTemplates.Names.GuestDeferredChargeFailed, data.GuestEmail, EmailTemplates.GuestDeferredChargeFailed(
                            culture,
                            data.GuestFirstName,
                            data.PropertyName,
                            data.CheckInDate,
                            data.CheckOutDate,
                            data.TotalPrice,
                            links.CheckoutOutcome(data.OrgSlug, data.BookingId, notice.CheckoutToken!),
                            cancelOnDate?.AddDays(-1)));
                        Queue(data.BookingId, EmailTemplates.Names.HostDeferredChargeFailed, data.HostEmail, EmailTemplates.HostDeferredChargeFailed(
                            culture, guestFullName, data.PropertyName, data.CheckInDate, data.CheckOutDate, data.TotalPrice, true, cancelOnDate, hostBookingUrl));
                        break;
                    }

                case DeferredChargeNoticeKind.ChargeNotAttempted:
                    Queue(data.BookingId, EmailTemplates.Names.HostDeferredChargeFailed, data.HostEmail, EmailTemplates.HostDeferredChargeFailed(
                        culture, guestFullName, data.PropertyName, data.CheckInDate, data.CheckOutDate, data.TotalPrice, false, null, hostBookingUrl));
                    break;

                case DeferredChargeNoticeKind.Cancelled:
                    Queue(data.BookingId, EmailTemplates.Names.GuestDeferredChargeCancelled, data.GuestEmail, EmailTemplates.GuestDeferredChargeCancelled(
                        culture, data.GuestFirstName, data.PropertyName, data.CheckInDate, data.CheckOutDate));
                    Queue(data.BookingId, EmailTemplates.Names.HostDeferredChargeCancelled, data.HostEmail, EmailTemplates.HostDeferredChargeCancelled(
                        culture, guestFullName, data.PropertyName, data.CheckInDate, data.CheckOutDate, hostBookingUrl));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Deferred charge emails ({Kind}) of booking {BookingId} could not be queued", notice.Kind, notice.BookingId);
        }
    }

    private void Queue(Guid bookingId, string template, string? to, EmailContent content)
    {
        if (!emailQueue.Enqueue(to, content, template))
            logger.LogWarning("Email {Template} for booking {BookingId} was not queued", template, bookingId);
    }

    private sealed record NoticeData(
        Guid BookingId,
        string GuestFirstName,
        string GuestLastName,
        string GuestEmail,
        string PropertyName,
        string OrgSlug,
        string HostEmail,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        decimal TotalPrice,
        DateTime? FailedAt);
}
