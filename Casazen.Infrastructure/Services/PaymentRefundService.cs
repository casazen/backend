using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Refunds of booking payments on Stripe Connect (BK-02, A3-05, A9-15). See <see cref="IPaymentRefundService"/>.
/// </summary>
/// <remarks>
/// <para><b>Charge model.</b> The checkout creates the PaymentIntent on the host's connected account
/// (<c>StripeService.CreateConnectedAccountPaymentIntentAsync</c> / <c>ChargePaymentMethodAsync</c> with the
/// <c>Stripe-Account</c> header, no application fee): direct charges. A refund is therefore created on the same account
/// (<see cref="Payment.StripeAccountId"/>, or the org's account for older rows) and is funded by its balance.</para>
/// <para><b>Exactly once.</b> The refundable amount is checked and a <see cref="PaymentRefundStatus.Pending"/> row is
/// written under a per-payment advisory lock, so two requests cannot refund the same euros. The row id is the Stripe
/// idempotency key: a timeout or a Stripe 5xx keeps the row pending and a Hangfire job resends the same request, which
/// Stripe answers with the same refund.</para>
/// <para><b>Status.</b> The payment becomes Refunded / PartiallyRefunded only from refunds that Stripe reports
/// <c>succeeded</c>, in its response or in a webhook; <c>pending</c> and <c>requires_action</c> keep the money reserved,
/// <c>failed</c> and <c>canceled</c> release it.</para>
/// </remarks>
public sealed class PaymentRefundService(
    AppDbContext db,
    IStripeService stripeService,
    IPaymentRefundRetryScheduler retryScheduler,
    IEmailQueue emailQueue,
    ILogger<PaymentRefundService> logger,
    TimeProvider? timeProvider = null) : IPaymentRefundService
{
    /// <summary>Metadata key that links a Stripe refund to its <see cref="PaymentRefund"/> row.</summary>
    internal const string RefundIdMetadataKey = "paymentRefundId";

    internal const string RefundKind = "booking-refund";

    /// <summary>
    /// Idempotency key prefix of the automatic refund of a payment that arrived when its booking could no longer be
    /// confirmed (BK-04): one key per PaymentIntent, unique in <c>PaymentRefunds</c> and sent to Stripe.
    /// </summary>
    internal const string LatePaymentRefundKeyPrefix = "late-payment-refund:";

    /// <summary>Idempotency key of the automatic late-payment refund of <paramref name="paymentIntentId"/>.</summary>
    internal static string LatePaymentRefundKey(string paymentIntentId) => LatePaymentRefundKeyPrefix + paymentIntentId;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<PaymentRefund> RefundAsync(PaymentRefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PaymentRefund refund;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db,
                         cancellationToken,
                         (PostgresAdvisoryLocks.Scope.PaymentRefund, request.PaymentId.ToString("N"))))
        {
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken)
                ?? throw PaymentNotFound(request.PaymentId);

            refund = await ReserveAsync(
                payment,
                request.Amount,
                PaymentRefundOrigin.Host,
                request.Reason,
                request.RequestedByUserId,
                cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        return await SubmitAsync(refund, throwOnTransientFailure: false, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentRefund>> GetRefundsAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        await db.PaymentRefunds
            .AsNoTracking()
            .Where(r => r.PaymentId == paymentId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<PaymentRefundSummary> GetSummaryAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw PaymentNotFound(paymentId);
        var refunds = await GetRefundsAsync(paymentId, cancellationToken);
        return Summarize(payment, refunds);
    }

    public async Task SubmitPendingAsync(Guid refundId, CancellationToken cancellationToken = default)
    {
        var refund = await db.PaymentRefunds.FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);
        if (refund is null)
        {
            logger.LogWarning("Refund {RefundId} to resubmit not found", refundId);
            return;
        }

        await SubmitAsync(refund, throwOnTransientFailure: true, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ApplyStripeRefundsAsync(
        IReadOnlyList<StripeRefundSnapshot> refunds,
        string? connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refunds);
        var succeeded = new List<Guid>();

        foreach (var group in refunds.Where(r => !string.IsNullOrWhiteSpace(r.PaymentIntentId)).GroupBy(r => r.PaymentIntentId!))
        {
            var payment = await FindPaymentForStripeEventAsync(group.Key, connectedAccountId, cancellationToken);
            if (payment is null)
                continue;

            foreach (var snapshot in group)
            {
                var refund = await FindRefundAsync(payment, snapshot, cancellationToken);
                if (refund is null)
                {
                    // Made outside CasaZen (Stripe Dashboard or API): recorded so the payment reflects it (A3-05).
                    refund = new PaymentRefund
                    {
                        PaymentId = payment.Id,
                        OrgId = payment.OrgId,
                        Origin = PaymentRefundOrigin.Stripe,
                        IdempotencyKey = $"stripe-refund:{snapshot.RefundId}",
                        CreatedAt = UtcNow,
                    };
                    db.PaymentRefunds.Add(refund);
                    logger.LogInformation(
                        "Refund {StripeRefundId} of payment {PaymentId} was made outside CasaZen: recorded",
                        snapshot.RefundId,
                        payment.Id);
                }

                if (Apply(refund, snapshot))
                    succeeded.Add(refund.Id);
            }

            await RecomputePaymentAsync(payment, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return succeeded;
    }

    public async Task<IReadOnlyList<Guid>> SyncPaymentIntentRefundsAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        // Only PaymentIntents of our payments: Stripe is not called for charges CasaZen does not know.
        if (await FindPaymentForStripeEventAsync(paymentIntentId, connectedAccountId, cancellationToken) is null)
            return [];

        var refunds = await stripeService.ListRefundsAsync(paymentIntentId, connectedAccountId, cancellationToken);
        return await ApplyStripeRefundsAsync(refunds.Select(ToSnapshot).ToList(), connectedAccountId, cancellationToken);
    }

    public async Task NotifyGuestAsync(Guid refundId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await ClaimGuestNotificationAsync(refundId, cancellationToken))
                return;

            var details = await db.PaymentRefunds
                .AsNoTracking()
                .Where(r => r.Id == refundId)
                .Select(r => new
                {
                    r.Amount,
                    r.IdempotencyKey,
                    r.Payment.Booking.CheckInDate,
                    r.Payment.Booking.CheckOutDate,
                    GuestFirstName = r.Payment.Booking.Guest.FirstName,
                    GuestEmail = r.Payment.Booking.Guest.Email,
                    PropertyName = r.Payment.Booking.Property.Name,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (details is null)
                return;

            // The automatic refund of a late payment tells the guest why the booking was not confirmed (BK-04).
            var latePayment = details.IdempotencyKey.StartsWith(LatePaymentRefundKeyPrefix, StringComparison.Ordinal);
            var email = latePayment
                ? EmailTemplates.GuestPaymentRefundedDatesUnavailable(
                    EmailTemplates.DefaultCulture,
                    details.GuestFirstName,
                    details.PropertyName,
                    details.CheckInDate,
                    details.CheckOutDate,
                    details.Amount)
                : EmailTemplates.GuestRefundConfirmed(
                    EmailTemplates.DefaultCulture,
                    details.GuestFirstName,
                    details.PropertyName,
                    details.CheckInDate,
                    details.Amount);
            var template = latePayment
                ? EmailTemplates.Names.GuestPaymentRefundedDatesUnavailable
                : EmailTemplates.Names.GuestRefundConfirmed;

            if (!emailQueue.Enqueue(details.GuestEmail, email, template))
                logger.LogWarning("Refund {RefundId} succeeded but the guest email was not queued", refundId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Guest email of refund {RefundId} could not be queued", refundId);
        }
    }

    /// <summary>
    /// Checks the refund against the payment and writes it as pending (the caller holds the payment's advisory lock
    /// and commits). Everything still refundable when <paramref name="amount"/> is null. The Stripe idempotency key is
    /// <c>payment-refund:{Id}</c> unless <paramref name="idempotencyKey"/> gives one (the late-payment refund keys it
    /// to its PaymentIntent, BK-04).
    /// </summary>
    internal async Task<PaymentRefund> ReserveAsync(
        Payment payment,
        decimal? amount,
        PaymentRefundOrigin origin,
        string? reason,
        string? requestedByUserId,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        if (payment.Status == PaymentStatus.Refunded)
            throw new DomainRuleException("payment_nothing_to_refund", "PaymentNothingToRefund");

        if (payment.Status is not (PaymentStatus.Completed or PaymentStatus.PartiallyRefunded))
            throw new DomainRuleException("payment_not_refundable", "PaymentNotRefundable");

        if (PaymentIntentIdOf(payment) is null)
            throw new DomainRuleException("payment_refund_offline", "PaymentRefundOffline");

        if (!payment.StripeIntentOnPlatform && await ResolveAccountAsync(payment, cancellationToken) is null)
            throw new DomainRuleException("payment_refund_account_missing", "PaymentRefundAccountMissing");

        var summary = Summarize(payment, await LoadRefundsAsync(payment.Id, cancellationToken));
        if (summary.RefundableAmount <= 0)
            throw new DomainRuleException("payment_nothing_to_refund", "PaymentNothingToRefund");

        var refundAmount = amount ?? summary.RefundableAmount;
        if (refundAmount <= 0 || decimal.Round(refundAmount, 2) != refundAmount)
            throw new DomainRuleException("refund_amount_invalid", "RefundAmountInvalid");

        if (refundAmount > summary.RefundableAmount)
        {
            throw new DomainRuleException(
                "refund_amount_exceeds_refundable",
                "RefundAmountExceedsRefundable",
                summary.RefundableAmount);
        }

        var now = UtcNow;
        var refund = new PaymentRefund
        {
            PaymentId = payment.Id,
            OrgId = payment.OrgId,
            Amount = refundAmount,
            Status = PaymentRefundStatus.Pending,
            Origin = origin,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim(), 500),
            RequestedByUserId = requestedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        refund.IdempotencyKey = idempotencyKey ?? $"payment-refund:{refund.Id:N}";
        db.PaymentRefunds.Add(refund);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Refund {RefundId} of {Amount} EUR reserved on payment {PaymentId} ({Origin})",
            refund.Id,
            refund.Amount,
            payment.Id,
            origin);
        return refund;
    }

    /// <summary>
    /// Sends a pending refund to Stripe with its idempotency key and applies the answer. A 4xx answer fails the refund
    /// (Stripe did not create it); a timeout or a 5xx keeps it pending and schedules a retry, or rethrows when
    /// <paramref name="throwOnTransientFailure"/> (the retry job itself).
    /// </summary>
    internal async Task<PaymentRefund> SubmitAsync(
        PaymentRefund refund,
        bool throwOnTransientFailure,
        CancellationToken cancellationToken)
    {
        if (refund.Status != PaymentRefundStatus.Pending || refund.StripeRefundId is not null)
            return refund;

        var payment = await db.Payments.FirstAsync(p => p.Id == refund.PaymentId, cancellationToken);
        var paymentIntentId = PaymentIntentIdOf(payment)!;
        var accountId = await ResolveAccountAsync(payment, cancellationToken);

        var request = new StripeRefundCreateRequest(
            paymentIntentId,
            accountId,
            ToCents(refund.Amount),
            refund.IdempotencyKey,
            new Dictionary<string, string>
            {
                [RefundIdMetadataKey] = refund.Id.ToString(),
                ["paymentId"] = payment.Id.ToString(),
                ["bookingId"] = payment.BookingId.ToString(),
                ["orgId"] = payment.OrgId.ToString(),
                ["kind"] = RefundKind,
            });

        Refund stripeRefund;
        try
        {
            stripeRefund = await stripeService.CreateRefundAsync(request, cancellationToken);
        }
        catch (StripeException ex) when (IsRejected(ex))
        {
            refund.Status = PaymentRefundStatus.Failed;
            refund.FailureReason = Truncate(ex.StripeError?.Code ?? ex.StripeError?.Type ?? $"http_{(int)ex.HttpStatusCode}", 100);
            refund.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(
                "Stripe rejected refund {RefundId} of payment {PaymentId}: {StripeErrorCode} (HTTP {StatusCode})",
                refund.Id,
                payment.Id,
                refund.FailureReason,
                (int)ex.HttpStatusCode);
            return refund;
        }
        catch (Exception ex) when (!throwOnTransientFailure && IsTransient(ex))
        {
            // Stripe may or may not have created it: the same idempotency key will tell.
            logger.LogWarning(ex, "Refund {RefundId} of payment {PaymentId} not confirmed by Stripe yet: retry scheduled", refund.Id, payment.Id);
            retryScheduler.ScheduleSubmit(refund.Id);
            return refund;
        }

        var succeeded = Apply(refund, ToSnapshot(stripeRefund));
        await RecomputePaymentAsync(payment, cancellationToken);
        await db.SaveChangesAsync(CancellationToken.None);

        if (succeeded)
            await NotifyGuestAsync(refund.Id, CancellationToken.None);

        return refund;
    }

    internal static PaymentRefundSummary Summarize(Payment payment, IEnumerable<PaymentRefund> refunds)
    {
        var list = refunds.ToList();
        var refunded = list.Where(r => r.Status == PaymentRefundStatus.Succeeded).Sum(r => r.Amount);
        var inProgress = list.Where(IsInProgress).Sum(r => r.Amount);
        var online = payment.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded
            && PaymentIntentIdOf(payment) is not null;
        var refundable = online ? Math.Max(0m, payment.Amount - refunded - inProgress) : 0m;
        return new PaymentRefundSummary(payment.Amount, refunded, inProgress, refundable, online);
    }

    internal static bool IsInProgress(PaymentRefund refund) =>
        refund.Status is PaymentRefundStatus.Pending or PaymentRefundStatus.RequiresAction;

    /// <summary>The PaymentIntent of a payment: the stored id, or the transaction id when it is one.</summary>
    internal static string? PaymentIntentIdOf(Payment payment)
    {
        if (!string.IsNullOrWhiteSpace(payment.StripePaymentIntentId))
            return payment.StripePaymentIntentId;

        return payment.TransactionId.StartsWith("pi_", StringComparison.Ordinal) ? payment.TransactionId : null;
    }

    /// <summary>
    /// The connected account of the payment's intent: the stored one, else the org's current account; null for a
    /// PaymentIntent of the platform account (<see cref="Payment.StripeIntentOnPlatform"/>, no <c>Stripe-Account</c>).
    /// </summary>
    internal async Task<string?> ResolveAccountAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.StripeIntentOnPlatform)
            return null;

        if (!string.IsNullOrWhiteSpace(payment.StripeAccountId))
            return payment.StripeAccountId;

        return await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == payment.OrgId)
            .Select(o => o.StripeConnectedAccountId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static decimal FromCents(long cents) => Math.Round(cents / 100m, 2);

    internal static StripeRefundSnapshot ToSnapshot(Refund refund) => new(
        refund.Id,
        refund.PaymentIntentId,
        refund.Amount,
        refund.Status,
        refund.FailureReason,
        refund.Metadata is null ? null : new Dictionary<string, string>(refund.Metadata));

    /// <summary>Applies a Stripe refund to its row; true when the row has just become succeeded.</summary>
    private bool Apply(PaymentRefund refund, StripeRefundSnapshot snapshot)
    {
        var wasSucceeded = refund.Status == PaymentRefundStatus.Succeeded;
        var status = MapStatus(snapshot.Status);

        refund.StripeRefundId ??= snapshot.RefundId;
        refund.Status = status;
        if (snapshot.AmountCents > 0)
            refund.Amount = FromCents(snapshot.AmountCents);
        refund.FailureReason = status is PaymentRefundStatus.Failed or PaymentRefundStatus.Canceled
            ? Truncate(snapshot.FailureReason ?? snapshot.Status ?? "failed", 100)
            : null;
        if (status == PaymentRefundStatus.Succeeded)
            refund.CompletedAt ??= UtcNow;
        refund.UpdatedAt = UtcNow;

        if (!wasSucceeded && status == PaymentRefundStatus.Succeeded)
        {
            logger.LogInformation("Refund {RefundId} ({StripeRefundId}) succeeded on Stripe", refund.Id, refund.StripeRefundId);
            return true;
        }

        if (status != PaymentRefundStatus.Succeeded && wasSucceeded)
            logger.LogWarning("Refund {RefundId} ({StripeRefundId}) moved from succeeded to {Status}", refund.Id, refund.StripeRefundId, status);

        return false;
    }

    private static PaymentRefundStatus MapStatus(string? status) => status switch
    {
        "succeeded" => PaymentRefundStatus.Succeeded,
        "failed" => PaymentRefundStatus.Failed,
        "canceled" => PaymentRefundStatus.Canceled,
        "requires_action" => PaymentRefundStatus.RequiresAction,
        _ => PaymentRefundStatus.Pending,
    };

    /// <summary>Refunded amount and status of the payment from its succeeded refunds only.</summary>
    internal async Task RecomputePaymentAsync(Payment payment, CancellationToken cancellationToken)
    {
        var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);
        var refunded = refunds.Where(r => r.Status == PaymentRefundStatus.Succeeded).Sum(r => r.Amount);

        payment.RefundedAmount = refunded;
        if (payment.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            payment.Status = refunded <= 0
                ? PaymentStatus.Completed
                : refunded >= payment.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        }

        payment.UpdatedAt = UtcNow;
    }

    /// <summary>Refunds of the payment, including the ones added to this context and not saved yet.</summary>
    private async Task<List<PaymentRefund>> LoadRefundsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var stored = await db.PaymentRefunds.Where(r => r.PaymentId == paymentId).ToListAsync(cancellationToken);
        var added = db.PaymentRefunds.Local.Where(r => r.PaymentId == paymentId && !stored.Contains(r));
        return stored.Concat(added).ToList();
    }

    private async Task<PaymentRefund?> FindRefundAsync(Payment payment, StripeRefundSnapshot snapshot, CancellationToken cancellationToken)
    {
        var local = db.PaymentRefunds.Local.FirstOrDefault(r => r.StripeRefundId == snapshot.RefundId);
        if (local is not null)
            return local;

        var byStripeId = await db.PaymentRefunds.FirstOrDefaultAsync(r => r.StripeRefundId == snapshot.RefundId, cancellationToken);
        if (byStripeId is not null)
            return byStripeId;

        // Created by CasaZen but the response never came back (timeout): linked through the metadata.
        if (snapshot.Metadata?.TryGetValue(RefundIdMetadataKey, out var raw) == true && Guid.TryParse(raw, out var refundId))
        {
            return await db.PaymentRefunds.FirstOrDefaultAsync(
                r => r.Id == refundId && r.PaymentId == payment.Id && r.StripeRefundId == null,
                cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// The payment of a PaymentIntent reported by a webhook, when the event comes from the account the intent was
    /// created on: the Connect endpoint for payments of a connected account (<paramref name="connectedAccountId"/> =
    /// the event's <c>account</c>), the platform endpoint only for payments without one.
    /// </summary>
    private async Task<Payment?> FindPaymentForStripeEventAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(
            p => p.StripePaymentIntentId == paymentIntentId || p.TransactionId == paymentIntentId,
            cancellationToken);
        if (payment is null)
        {
            logger.LogInformation("Refund event for unknown payment intent {PaymentIntentId}: ignored", paymentIntentId);
            return null;
        }

        var expectedAccount = await ResolveAccountAsync(payment, cancellationToken);
        if (connectedAccountId is null)
        {
            if (string.IsNullOrWhiteSpace(payment.StripeAccountId))
                return payment;
        }
        else if (!payment.StripeIntentOnPlatform &&
                 (expectedAccount is null || string.Equals(expectedAccount, connectedAccountId, StringComparison.Ordinal)))
        {
            payment.StripeAccountId ??= connectedAccountId;
            return payment;
        }

        logger.LogWarning(
            "Refund event of payment {PaymentId} came from account {EventAccount}, expected {ExpectedAccount}: ignored",
            payment.Id,
            connectedAccountId ?? "platform",
            expectedAccount ?? "platform");
        return null;
    }

    private async Task<bool> ClaimGuestNotificationAsync(Guid refundId, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        if (db.Database.IsRelational())
        {
            // Atomic: the synchronous answer and the webhook may both see the refund succeed.
            var claimed = await db.PaymentRefunds
                .Where(r => r.Id == refundId && r.Status == PaymentRefundStatus.Succeeded && r.GuestNotifiedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.GuestNotifiedAt, now), cancellationToken);
            return claimed == 1;
        }

        var refund = await db.PaymentRefunds.FirstOrDefaultAsync(
            r => r.Id == refundId && r.Status == PaymentRefundStatus.Succeeded && r.GuestNotifiedAt == null,
            cancellationToken);
        if (refund is null)
            return false;

        refund.GuestNotifiedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Stripe answered and did not create the refund (4xx other than 429).</summary>
    private static bool IsRejected(StripeException ex)
    {
        var status = (int)ex.HttpStatusCode;
        return status is >= 400 and < 500 && ex.HttpStatusCode != HttpStatusCode.TooManyRequests;
    }

    /// <summary>No answer, or an answer that does not say whether the refund exists: retry with the same key.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is StripeException or HttpRequestException or TimeoutException or OperationCanceledException;

    private static NotFoundException PaymentNotFound(Guid paymentId) =>
        new($"Payment {paymentId} not found") { Code = "payment_not_found", MessageKey = "PaymentNotFound" };

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
