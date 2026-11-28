using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The money CasaZen holds, from the block of the card to the day the request is closed (SP-15c-a, <see cref="ISupplierFundsService"/>):
/// the PaymentIntent of a booking hold, the payment born with the request, the charge when the supplier accepts, and the money given back
/// when the request is refused, cancelled or lapses. The transfer to the supplier is in <c>.FundsRelease.cs</c>, the events in
/// <c>.FundsWebhook.cs</c>, the job in <c>.FundsJobs.cs</c>.
/// </summary>
/// <remarks>
/// <para><b>Every Stripe step is idempotent and every state is re-read from Stripe when it matters.</b> The block is made with the key of the
/// hold, the charge with the key of the payment, the cancellation and the refunds with their own keys; a step that finds the PaymentIntent
/// in another state (<c>payment_intent_unexpected_state</c>) is answered with the real state, and the DB follows it. When a step succeeds on
/// Stripe and its database change is rolled back with a caller that failed afterwards (the take lost a race), the webhook and the funds job
/// bring the database to what Stripe says: money is never lost, only late.</para>
/// <para><b>Locks.</b> The advisory lock of the payment (key: the request) is taken after the supplier's calendar lock when both are needed
/// (a take), never the other way round, so the two cannot wait for each other. When the caller has a transaction open the lock joins it.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    private enum AuthorizationOutcome
    {
        Canceled,

        Captured,

        Pending,
    }

    public async Task<bool> CanAuthorizeAsync(Guid supplierOrgId, int amountCents, CancellationToken cancellationToken = default)
    {
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments) || amountCents < options.Value.MinAmountCents)
            return false;

        if (string.IsNullOrWhiteSpace(configuration["Stripe:PublishableKey"]) || platformGateway is null)
            return false;

        return (await ReadSupplierAsync(supplierOrgId, cancellationToken)).CanReceivePayments;
    }

    public async Task<HoldAuthorization> AuthorizeHoldAsync(HoldAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.AmountCents, 1);

        var charge = await Platform.CreateAsync(
            new PlatformChargeRequest(
                request.AmountCents,
                ServiceCharges.Currency,
                ManualCapture: true,
                ServiceCharges.HoldTransferGroup(request.HoldId),
                new Dictionary<string, string>
                {
                    [ServiceCharges.HoldMetadataKey] = request.HoldId.ToString(),
                    [ServiceCharges.SupplierMetadataKey] = request.SupplierOrgId.ToString(),
                },
                ServiceCharges.HoldAuthorizationIdempotencyKey(request.HoldId),
                request.Description),
            cancellationToken);

        logger.LogInformation(
            "Hold {HoldId}: PaymentIntent {PaymentIntentId} created to block {AmountCents} cents on the customer's card",
            request.HoldId, charge.Id, request.AmountCents);
        return ToAuthorization(charge) ?? throw new PaymentProcessingException("Stripe returned a PaymentIntent without a client secret.");
    }

    public async Task<HoldAuthorization?> ReadHoldAuthorizationAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        var charge = await ReadPlatformIntentAsync(paymentIntentId, cancellationToken);

        return charge is not null && ServiceCharges.IsPayable(charge.Status) ? ToAuthorization(charge) : null;
    }

    public async Task CancelHoldAuthorizationAsync(Guid holdId, string paymentIntentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        PlatformCharge charge;
        try
        {
            charge = await Platform.CancelAsync(
                paymentIntentId, ServiceCharges.HoldCancellationIdempotencyKey(holdId, paymentIntentId), cancellationToken);
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return;
        }

        if (charge.Status == "succeeded")
        {
            logger.LogError(
                "Hold {HoldId}: PaymentIntent {PaymentIntentId} could not be released because it was charged. Refund it on the Stripe Dashboard",
                holdId, paymentIntentId);
            return;
        }

        logger.LogInformation("Hold {HoldId}: the block {PaymentIntentId} was released ({Status})", holdId, paymentIntentId, charge.Status);
    }

    private HoldAuthorization? ToAuthorization(PlatformCharge charge) =>
        string.IsNullOrWhiteSpace(charge.ClientSecret)
            ? null
            : new HoldAuthorization(
                charge.Id,
                charge.ClientSecret,
                configuration["Stripe:PublishableKey"] ?? string.Empty,
                (int)Math.Min(charge.AmountCents, int.MaxValue),
                charge.Currency.ToUpperInvariant());

    private async Task<PlatformCharge?> ReadPlatformIntentAsync(string paymentIntentId, CancellationToken cancellationToken)
    {
        try
        {
            return await Platform.GetAsync(paymentIntentId, cancellationToken);
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            logger.LogWarning("PaymentIntent {PaymentIntentId} no longer exists on Stripe", paymentIntentId);
            return null;
        }
    }

    public async Task<ServiceRequestPayment> AddAuthorizedPaymentAsync(
        ServiceRequest request,
        AuthorizedPaymentInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PaymentIntentId);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.AmountCents, 1);

        var percent = await ReadCommissionPercentAsync(request.SupplierOrgId, cancellationToken);
        var fee = SupplierCommission.FeeCents(input.AmountCents, percent);
        var (payerKind, payerOrgId) = PayerOf(request);
        var now = Now();

        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = request.Id,
            SupplierOrgId = request.SupplierOrgId,
            PayerKind = payerKind,
            PayerOrgId = payerOrgId,
            AmountCents = input.AmountCents,
            Currency = ServiceCharges.Currency,
            CommissionPercent = percent,

            ApplicationFeeCents = fee,
            NetCents = SupplierCommission.NetCents(input.AmountCents, fee),
            Status = ServicePaymentStatus.Authorized,
            FundsModel = ServiceFundsModel.PlatformHeld,
            StripePaymentIntentId = input.PaymentIntentId,
            ConnectedAccountId = null,
            PaymentIntentCount = 1,
            TransferGroup = input.TransferGroup,
            AuthorizedAt = input.AuthorizedAt,
            AuthorizationExpiresAt = HeldPaymentRules.AuthorizationExpiry(input.AuthorizedAt, options.Value.AuthorizationValidity),
            LineItemsJson = ServiceRequestJson.Serialize(input.Lines),
            CreatedAt = now,
            UpdatedAt = now,
        };

        request.PaymentMode = ServiceRequestPaymentMode.Online;
        db.ServiceRequestPayments.Add(payment);
        return payment;
    }

    public DateTime LatestResponseAt(DateTime authorizedAt) => HeldPaymentRules.LatestResponseAt(authorizedAt, options.Value.AuthorizationValidity);

    public async Task<bool> HasHeldPaymentAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.ServiceRequestId == requestId && p.FundsModel == ServiceFundsModel.PlatformHeld)
            .AnyAsync(cancellationToken);

    public async Task<ServiceRequestPayment?> FindHeldPaymentAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.ServiceRequestId == requestId && p.FundsModel == ServiceFundsModel.PlatformHeld)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<(Guid Id, ServicePaymentStatus Status)?> FindLatestHeldAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var row = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.ServiceRequestId == requestId && p.FundsModel == ServiceFundsModel.PlatformHeld)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Status })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Id, row.Status);
    }

    private async Task<ServiceRequestStatus?> ReadRequestStatusAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => (ServiceRequestStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The plan of a showcase request that is completed: with a payment held by CasaZen, nothing is created (the payment exists since the
    /// booking) and the moment the money is due is fixed on it; the amount is the one the customer blocked, so the supplier cannot declare
    /// another (422 <see cref="ServicePaymentErrors.AmountLocked"/>). Without one, the request is completed as a manual one, as before.
    /// </summary>
    private async Task<ServicePaymentPlan> PlanHeldCompletionAsync(ServiceRequest request, int? finalAmountCents, CancellationToken cancellationToken)
    {
        var payment = await db.ServiceRequestPayments
            .Where(p => p.ServiceRequestId == request.Id
                        && p.FundsModel == ServiceFundsModel.PlatformHeld
                        && p.Status != ServicePaymentStatus.Canceled)
            .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            logger.LogWarning(
                "ServiceRequest {Id}: a request from a supplier's showcase with no held payment is completed as a manual one", request.Id);
            return new ServicePaymentPlan(ServiceRequestPaymentMode.Manual, null, null);
        }

        if (finalAmountCents != payment.AmountCents)
            throw new DomainRuleException(ServicePaymentErrors.AmountLocked, ServicePaymentErrors.AmountLockedMessageKey);

        if (payment.Status == ServicePaymentStatus.Authorized)
        {
            await CaptureForTakeAsync(request, cancellationToken);
            payment = await db.ServiceRequestPayments
                .Where(p => p.Id == payment.Id)
                .FirstAsync(cancellationToken);
        }

        if (payment.Status == ServicePaymentStatus.Disputed)
            throw ServiceDisputeErrors.CompletionBlockedByDispute();

        if (payment.Status is not (ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed))
            throw InFlight();

        // Fixed once: completing is a transition that happens once, and a retry of it must not push the moment further.
        if (payment.ReleaseAt is null)
        {
            var now = Now();
            payment.ReleaseAt = HeldPaymentRules.ReleaseAt(now, payment.CapturedAt ?? now, options.Value.ReleaseAfter);
            payment.UpdatedAt = now;
        }

        return new ServicePaymentPlan(ServiceRequestPaymentMode.Online, null, null);
    }

    public async Task CaptureForTakeAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var latest = await FindLatestHeldAsync(request.Id, cancellationToken);
        if (latest is not { } known)
            return;

        if (known.Status == ServicePaymentStatus.Canceled)
            throw AuthorizationExpired();

        await using var transaction = await LockAsync(request.Id, cancellationToken);

        var payment = await LoadPaymentAsync(known.Id, cancellationToken);
        if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld })
            return;

        switch (payment.Status)
        {
            case ServicePaymentStatus.Held
                or ServicePaymentStatus.Disputed
                or ServicePaymentStatus.TransferFailed
                or ServicePaymentStatus.Released
                or ServicePaymentStatus.PartiallyRefunded
                or ServicePaymentStatus.Refunded:
                // Charged already: a retry of the take, or the webhook was first. Charging twice is what must never happen.
                return;
            case ServicePaymentStatus.Canceled:
                throw AuthorizationExpired();
            case ServicePaymentStatus.Authorized:
                break;
            default:
                throw CaptureFailed();
        }

        var now = Now();
        if (!HeldPaymentRules.CanCapture(payment.Status, payment.AuthorizationExpiresAt, now))
        {
            logger.LogInformation("Payment {PaymentId}: the block lapsed before the supplier accepted, it is released", payment.Id);
            var lapsed = await DropBlockAsync(payment, now, transaction, cancellationToken);
            if (lapsed == AuthorizationOutcome.Captured)
                return;

            throw AuthorizationExpired();
        }

        PlatformCharge charge;
        try
        {
            charge = await Platform.CaptureAsync(
                payment.StripePaymentIntentId!, ServiceCharges.CaptureIdempotencyKey(payment.Id), cancellationToken);
        }
        catch (Stripe.StripeException ex)
        {
            logger.LogWarning(
                ex, "Payment {PaymentId}: the card could not be charged ({StripeCode}); the request is not accepted",
                payment.Id, ex.StripeError?.Code ?? ex.StripeError?.Type ?? $"http_{(int)ex.HttpStatusCode}");

            if (IsDefinitiveCaptureFailure(ex))
            {
                _ = await DropBlockAsync(payment, now, transaction, cancellationToken);
                throw AuthorizationExpired();
            }

            throw CaptureFailed();
        }

        switch (charge.Status)
        {
            case "succeeded":
                break;
            case "canceled":
                payment.Status = ServicePaymentStatus.Canceled;
                payment.CanceledAt = now;
                payment.UpdatedAt = now;
                await SaveAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                throw AuthorizationExpired();
            default:
                logger.LogWarning("Payment {PaymentId}: after the capture the PaymentIntent is {Status}", payment.Id, charge.Status);
                throw CaptureFailed();
        }

        var mismatches = HeldChargeVerification.CaptureMismatches(ExpectationOf(payment), ObservationOf(charge));
        if (mismatches.Count > 0)
        {
            var code = ServiceChargeVerification.ReviewCode(mismatches);
            payment.Status = ServicePaymentStatus.NeedsReview;
            payment.FailureCode = Truncate(code, 100);
            payment.UpdatedAt = now;
            logger.LogError(
                "Held payment {PaymentId} NOT recorded as held: PaymentIntent {PaymentIntentId} differs from what was asked for ({Mismatches})",
                payment.Id, charge.Id, code);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            await CompleteAsync([new ServicePaymentNotice(ServicePaymentNoticeKind.NeedsReview, payment.Id, Detail: code)], CancellationToken.None);
            throw CaptureFailed();
        }

        RecordCaptured(payment, charge.LatestChargeId, now);
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation(
            "Payment {PaymentId} of request {RequestId}: the card was charged and the money is held by CasaZen", payment.Id, request.Id);
    }

    /// <summary>
    /// Releases the block of a payment whose take is being refused, and saves it. A failure of Stripe or of the database here is logged and
    /// swallowed: the take is refused all the same, and the funds job drops the block on its next run (the block is never kept for good).
    /// </summary>
    private async Task<AuthorizationOutcome> DropBlockAsync(
        ServiceRequestPayment payment,
        DateTime now,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await TryCancelAuthorizationAsync(payment, now, cancellationToken);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Payment {PaymentId}: the block could not be released now; the funds job will do it", payment.Id);
            return AuthorizationOutcome.Pending;
        }
    }

    private void RecordCaptured(ServiceRequestPayment payment, string? chargeId, DateTime now)
    {
        payment.Status = ServicePaymentStatus.Held;
        payment.CapturedAt ??= now;
        payment.PaidAt ??= now;
        payment.PaidVia = ServicePaymentChannel.Stripe;
        payment.StripeChargeId = chargeId ?? payment.StripeChargeId;
        payment.FailureCode = null;
        payment.UpdatedAt = now;

        if (payment.PayerKind == ServicePayerKind.Host)
            payment.ReleaseAt ??= HeldPaymentRules.ReleaseAt(payment.CreatedAt, payment.CapturedAt.Value, options.Value.ReleaseAfter);
    }

    private static bool IsDefinitiveCaptureFailure(Stripe.StripeException ex) =>
        ex.StripeError?.Type == "card_error" || ex.StripeError?.Code is "charge_expired_for_capture" or "payment_intent_unexpected_state";

    private static HeldChargeExpectation ExpectationOf(ServiceRequestPayment payment) =>
        new(payment.StripePaymentIntentId, payment.AmountCents, payment.Currency);

    private static HeldChargeObservation ObservationOf(PlatformCharge charge, string? accountId = null) => new(
        charge.Id, accountId, charge.AmountCents, charge.AmountCapturableCents, charge.AmountReceivedCents, charge.Currency, charge.ApplicationFeeCents);

    /// <summary>
    /// Releases the block of a payment (under its lock): Stripe cancels the PaymentIntent and the payment becomes <c>Canceled</c>. If the card
    /// was charged meanwhile it is recorded as held (the caller gives the money back); if the PaymentIntent is still a block, nothing changes.
    /// The caller saves.
    /// </summary>
    private async Task<AuthorizationOutcome> TryCancelAuthorizationAsync(ServiceRequestPayment payment, DateTime now, CancellationToken cancellationToken)
    {
        PlatformCharge? charge;
        try
        {
            charge = await Platform.CancelAsync(
                payment.StripePaymentIntentId!,
                ServiceCharges.CancellationIdempotencyKey(payment.Id, payment.StripePaymentIntentId!),
                cancellationToken);
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            charge = null;
        }

        if (charge is null || charge.Status == "canceled")
        {
            payment.Status = ServicePaymentStatus.Canceled;
            payment.CanceledAt = now;
            payment.UpdatedAt = now;
            return AuthorizationOutcome.Canceled;
        }

        if (charge.Status == "succeeded")
        {
            var mismatches = HeldChargeVerification.CaptureMismatches(ExpectationOf(payment), ObservationOf(charge));
            if (mismatches.Count > 0)
            {
                payment.Status = ServicePaymentStatus.NeedsReview;
                payment.FailureCode = Truncate(ServiceChargeVerification.ReviewCode(mismatches), 100);
                payment.UpdatedAt = now;
                return AuthorizationOutcome.Pending;
            }

            RecordCaptured(payment, charge.LatestChargeId, now);
            return AuthorizationOutcome.Captured;
        }

        logger.LogWarning("Payment {PaymentId}: the block is still {Status} after the cancellation", payment.Id, charge.Status);
        return AuthorizationOutcome.Pending;
    }

    public async Task SettleClosedRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        try
        {
            var held = await FindLatestHeldAsync(requestId, cancellationToken);
            if (held is { } known && known.Status != ServicePaymentStatus.Canceled)
                await SettleClosedPaymentAsync(new PaymentRef(known.Id, requestId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingPaymentChanges();
            logger.LogError(
                ex, "The money of the closed request {RequestId} could not be settled now; the funds job will try again", requestId);
        }
    }

    /// <summary>
    /// Settles the money of a payment whose request is closed (refused, cancelled, lapsed): releases the block, or gives the charged money
    /// back in full (decision D6: cancelling is always free, the commission included). A request that is not closed, or a payment that is
    /// not open, is left alone. True when it did anything.
    /// </summary>
    private async Task<bool> SettleClosedPaymentAsync(PaymentRef candidate, CancellationToken cancellationToken)
    {
        var refundAll = false;
        await using (var transaction = await LockAsync(candidate.ServiceRequestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld })
                return false;

            if (await ReadRequestStatusAsync(payment.ServiceRequestId, cancellationToken)
                is not (ServiceRequestStatus.Annullato or ServiceRequestStatus.Rifiutato))
                return false;

            var now = Now();
            switch (payment.Status)
            {
                case ServicePaymentStatus.Authorized or ServicePaymentStatus.Requested or ServicePaymentStatus.Failed
                    when payment.Status == ServicePaymentStatus.Authorized || payment.PayerKind == ServicePayerKind.Private:
                    var outcome = await TryCancelAuthorizationAsync(payment, now, cancellationToken);
                    await SaveAsync(cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                    if (outcome == AuthorizationOutcome.Canceled)
                    {
                        logger.LogInformation("Payment {PaymentId}: the request closed, the block on the card was released", payment.Id);
                        return true;
                    }

                    refundAll = outcome == AuthorizationOutcome.Captured;
                    break;

                case ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed:
                    refundAll = true;
                    break;

                default:
                    return false;
            }
        }

        if (!refundAll)
            return false;

        await RefundHeldAsync(
            candidate.Id,
            amountCents: null,
            reason: "request_closed",
            ServicePaymentRefundOrigin.System,
            actorUserId: null,
            cancellationToken);
        return true;
    }

    private void DetachPendingPaymentChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(e => e.Entity is ServiceRequestPayment or ServiceRequestPaymentRefund && e.State is EntityState.Modified or EntityState.Added)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static DomainConflictException CaptureFailed() =>
        new(ServicePaymentErrors.CaptureFailed, ServicePaymentErrors.CaptureFailedMessageKey);

    private static DomainConflictException AuthorizationExpired() =>
        new(ServicePaymentErrors.AuthorizationExpired, ServicePaymentErrors.AuthorizationExpiredMessageKey);

    private static DomainConflictException HeldByCasaZen() =>
        new(ServicePaymentErrors.HeldByCasaZen, ServicePaymentErrors.HeldByCasaZenMessageKey);
}
