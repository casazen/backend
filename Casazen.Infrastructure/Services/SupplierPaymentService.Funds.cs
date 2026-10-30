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
    /// <summary>What the cancellation of a block found on Stripe.</summary>
    private enum AuthorizationOutcome
    {
        /// <summary>The block is released: the payment is <c>Canceled</c>.</summary>
        Canceled,

        /// <summary>The card had been charged meanwhile: the payment is <c>Held</c> now and the money must be given back, not dropped.</summary>
        Captured,

        /// <summary>The PaymentIntent is still a block: the cancellation did not take effect, try again later.</summary>
        Pending,
    }

    // ─── The card of a booking, before the request exists ────────────────────────────────────────────────────────────

    public async Task<bool> CanAuthorizeAsync(Guid supplierOrgId, int amountCents, CancellationToken cancellationToken = default)
    {
        // The flag stops the creation only: what is in flight is followed whatever it says.
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments) || amountCents < options.Value.MinAmountCents)
            return false;

        // Without the publishable key the browser cannot confirm the card: the booking goes on as it always did.
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

        // Only a PaymentIntent the customer can still pay is given back: a secret of one that is blocked or canceled is of no use.
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
            // Gone with nothing to release.
            return;
        }

        if (charge.Status == "succeeded")
        {
            // A block of a hold is never charged by CasaZen before the request exists; if it was, somebody did it outside: say so loudly.
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

    /// <summary>The PaymentIntent on the platform as Stripe has it now, or null when it no longer exists. Any other Stripe failure is rethrown.</summary>
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

    // ─── The payment born with the request ───────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestPayment> AddAuthorizedPaymentAsync(
        ServiceRequest request,
        AuthorizedPaymentInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PaymentIntentId);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.AmountCents, 1);

        // The commission is snapshotted now, with the percentage in force now (the supplier's own, else the platform's).
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

            // The commission CasaZen keeps: nothing is sent to Stripe as a fee, it is the part that is not transferred.
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

        // The request is paid inside CasaZen from its birth: the supplier's take charges the card, and nothing can be marked paid by hand.
        request.PaymentMode = ServiceRequestPaymentMode.Online;
        db.ServiceRequestPayments.Add(payment);
        return payment;
    }

    public DateTime LatestResponseAt(DateTime authorizedAt) => HeldPaymentRules.LatestResponseAt(authorizedAt, options.Value.AuthorizationValidity);

    // ─── Reading ─────────────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>The most recent payment of the request that CasaZen holds (whatever its status, even a canceled block), as a reference.</summary>
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

    /// <summary>The status of the request, whoever it belongs to; null when it does not exist. The request has two parties and no tenant filter: scoped by its id.</summary>
    private async Task<ServiceRequestStatus?> ReadRequestStatusAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => (ServiceRequestStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

    // ─── Completing the work ─────────────────────────────────────────────────────────────────────────────────────────

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

        // The price is what the customer blocked on the card; a different total, or extras, would be money nobody agreed to.
        if (finalAmountCents != payment.AmountCents)
            throw new DomainRuleException(ServicePaymentErrors.AmountLocked, ServicePaymentErrors.AmountLockedMessageKey);

        // A block that was never charged (the take did not charge it) is charged now: the work is done, the money must be held.
        if (payment.Status == ServicePaymentStatus.Authorized)
        {
            await CaptureForTakeAsync(request, cancellationToken);
            payment = await db.ServiceRequestPayments
                .Where(p => p.Id == payment.Id)
                .FirstAsync(cancellationToken);
        }

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

    // ─── Charging the card when the supplier accepts ─────────────────────────────────────────────────────────────────

    public async Task CaptureForTakeAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Cheap, and the usual answer: most requests (every host's, every booking without a card) have no held payment.
        var latest = await FindLatestHeldAsync(request.Id, cancellationToken);
        if (latest is not { } known)
            return;

        // A block that was dropped (lapsed, canceled, refused) leaves a booking nobody pays: it cannot be accepted as a manual one.
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
            // The block outlived its usable time: it is released, and the booking cannot be accepted.
            logger.LogInformation("Payment {PaymentId}: the block lapsed before the supplier accepted, it is released", payment.Id);
            var lapsed = await DropBlockAsync(payment, now, transaction, cancellationToken);
            if (lapsed == AuthorizationOutcome.Captured)
                return; // charged just now by somebody else: the money is held, nothing is refused

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

            // A refusal that will not change (the bank, an expired block) drops the block; a failure of the moment (the network,
            // a busy Stripe) leaves it, and the supplier can try again.
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
                // Still a block, or in a state a card never reaches here: nothing was charged, the take is refused.
                logger.LogWarning("Payment {PaymentId}: after the capture the PaymentIntent is {Status}", payment.Id, charge.Status);
                throw CaptureFailed();
        }

        var mismatches = HeldChargeVerification.CaptureMismatches(ExpectationOf(payment), ObservationOf(charge));
        if (mismatches.Count > 0)
        {
            // Money moved in a way nobody planned: held for an admin, the take is refused, the request is not shown as paid.
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

    /// <summary>The card was charged: the money is held. <c>PaidAt</c> is the day the payer paid; it moves to the day of the release.</summary>
    private static void RecordCaptured(ServiceRequestPayment payment, string? chargeId, DateTime now)
    {
        payment.Status = ServicePaymentStatus.Held;
        payment.CapturedAt ??= now;
        payment.PaidAt ??= now;
        payment.PaidVia = ServicePaymentChannel.Stripe;
        payment.StripeChargeId = chargeId ?? payment.StripeChargeId;
        payment.FailureCode = null;
        payment.UpdatedAt = now;
    }

    /// <summary>A refusal of the capture that a second try will not change: the bank's, or a block that expired.</summary>
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
            // Gone: nothing is blocked any more.
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

    // ─── Giving the money back when the request closes ───────────────────────────────────────────────────────────────

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
            // The request is closed already and must stay so: the funds job settles the money on its next run.
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
                case ServicePaymentStatus.Authorized:
                    var outcome = await TryCancelAuthorizationAsync(payment, now, cancellationToken);
                    await SaveAsync(cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                    if (outcome == AuthorizationOutcome.Canceled)
                    {
                        logger.LogInformation("Payment {PaymentId}: the request closed, the block on the card was released", payment.Id);
                        return true;
                    }

                    // Charged meanwhile: the money is held now, and goes back below.
                    refundAll = outcome == AuthorizationOutcome.Captured;
                    break;

                case ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed:
                    refundAll = true;
                    break;

                default:
                    // Released (the work was paid), refunded, in dispute, in review: not for an automatic refund.
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

    /// <summary>Forgets the unsaved changes of payments and refunds left in the context by a step that failed: the caller's other work is untouched.</summary>
    private void DetachPendingPaymentChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(e => e.Entity is ServiceRequestPayment or ServiceRequestPaymentRefund && e.State is EntityState.Modified or EntityState.Added)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // ─── Errors ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static DomainConflictException CaptureFailed() =>
        new(ServicePaymentErrors.CaptureFailed, ServicePaymentErrors.CaptureFailedMessageKey);

    private static DomainConflictException AuthorizationExpired() =>
        new(ServicePaymentErrors.AuthorizationExpired, ServicePaymentErrors.AuthorizationExpiredMessageKey);

    private static DomainConflictException HeldByCasaZen() =>
        new(ServicePaymentErrors.HeldByCasaZen, ServicePaymentErrors.HeldByCasaZenMessageKey);
}
