using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The job of the money CasaZen holds (SP-15c-a, <see cref="ISupplierFundsJobService"/>, <c>service-funds</c> every five minutes): it releases
/// to the supplier the money whose time has come, tries again the transfers that failed, settles the money of the requests that closed, drops the
/// blocks that outlived their time, and warns the admins about money held for too long.
/// </summary>
/// <remarks>
/// <para>It is the safety net of every step that happens in a request: a step whose database change was rolled back with a caller that failed, a
/// refund that could not be asked of Stripe at the moment, a block that nobody dropped. It does not decide anything new: each payment is handled
/// by the same methods as the request that would have handled it, under the payment lock of its request and read again after it, with the change
/// tracker cleared before it, so a run that overlaps another run, a webhook or a payer's "all fine" finds the payment already changed and skips
/// it. An error on one payment is logged and counted, and the others go on.</para>
/// <para>Always scheduled, whatever the state of the flag <c>SupplierOnlinePayments</c>: it follows money that is already in flight.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    public async Task<ServiceFundsRun> RunFundsAsync(CancellationToken cancellationToken = default)
    {
        if (platformGateway is null)
            return ServiceFundsRun.Empty;

        int released = 0, releaseFailed = 0, settled = 0, lapsed = 0, warnings = 0, errors = 0;

        // 1. The money whose time has come, and the transfers that failed and are due for another try.
        var now = Now();
        var due = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.FundsModel == ServiceFundsModel.PlatformHeld
                        && (p.Status == ServicePaymentStatus.Held || p.Status == ServicePaymentStatus.TransferFailed)
                        && p.ReleaseAt != null
                        && p.ReleaseAt <= now
                        && (p.TransferNextAttemptAt == null || p.TransferNextAttemptAt <= now))
            .OrderBy(p => p.ReleaseAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        foreach (var candidate in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await ReleaseAsync(candidate, confirmedByPayer: false, clearTracker: true, cancellationToken))
                {
                    case ReleaseResult.Released:
                        released++;
                        break;
                    case ReleaseResult.Waiting or ReleaseResult.Refused or ReleaseResult.NeedsReview:
                        releaseFailed++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Held payment {PaymentId} could not be released; the next run tries again", candidate.Id);
            }
        }

        // 2. The money of the requests that closed: the block released, the charge given back.
        var closed = await (
                from p in db.ServiceRequestPayments
                    .AsNoTracking()
                    .Where(p => p.FundsModel == ServiceFundsModel.PlatformHeld
                                && (p.Status == ServicePaymentStatus.Authorized
                                    || p.Status == ServicePaymentStatus.Held
                                    || p.Status == ServicePaymentStatus.TransferFailed))
                join r in db.ServiceRequests.IgnoreQueryFilters().AsNoTracking() on p.ServiceRequestId equals r.Id
                where r.Status == ServiceRequestStatus.Annullato || r.Status == ServiceRequestStatus.Rifiutato
                orderby p.UpdatedAt
                select new PaymentRef(p.Id, p.ServiceRequestId))
            .Take(ServicePaymentLimits.SyncBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in closed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                db.ChangeTracker.Clear();
                if (await SettleClosedPaymentAsync(candidate, cancellationToken))
                    settled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The money of the closed request {RequestId} could not be settled; the next run tries again", candidate.ServiceRequestId);
            }
        }

        // 3. The blocks that outlived their usable time: released, so no booking is left waiting on a card that may not be there.
        var expired = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.FundsModel == ServiceFundsModel.PlatformHeld
                        && p.Status == ServicePaymentStatus.Authorized
                        && p.AuthorizationExpiresAt != null
                        && p.AuthorizationExpiresAt <= now)
            .OrderBy(p => p.AuthorizationExpiresAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        foreach (var candidate in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await LapseAuthorizationAsync(candidate, cancellationToken))
                    lapsed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The lapsed block of payment {PaymentId} could not be released; the next run tries again", candidate.Id);
            }
        }

        // 4. Money held for nearly the longest CasaZen means to hold it: the admins are warned once, nothing moves.
        var warnBefore = now.AddDays(-(options.Value.MaxHoldDays - options.Value.HoldWarningDays));
        var old = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.FundsModel == ServiceFundsModel.PlatformHeld
                        && (p.Status == ServicePaymentStatus.Held
                            || p.Status == ServicePaymentStatus.TransferFailed
                            || p.Status == ServicePaymentStatus.Disputed)
                        && p.HoldWarnedAt == null
                        && p.CapturedAt != null
                        && p.CapturedAt <= warnBefore)
            .OrderBy(p => p.CapturedAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        foreach (var candidate in old)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await WarnAboutHoldAgeAsync(candidate, cancellationToken))
                    warnings++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The hold warning of payment {PaymentId} could not be recorded; the next run tries again", candidate.Id);
            }
        }

        return new ServiceFundsRun(released, releaseFailed, settled, lapsed, warnings, errors);
    }

    /// <summary>
    /// Releases the block of a payment that outlived <c>AuthorizationValidityHours</c>, under its lock, if it is still an unused block when the
    /// lock is held. A card that was charged meanwhile is recorded as held (the money is not dropped, and a request that is still open keeps it).
    /// True when the block was released.
    /// </summary>
    private async Task<bool> LapseAuthorizationAsync(PaymentRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await LockAsync(candidate.ServiceRequestId, cancellationToken);

        var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
        var now = Now();
        if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld, Status: ServicePaymentStatus.Authorized }
            || !HeldPaymentRules.AuthorizationLapsed(payment.AuthorizationExpiresAt, now))
            return false;

        var outcome = await TryCancelAuthorizationAsync(payment, now, cancellationToken);
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        if (outcome == AuthorizationOutcome.Canceled)
            logger.LogInformation("Payment {PaymentId}: the block outlived its usable time and was released", payment.Id);

        return outcome == AuthorizationOutcome.Canceled;
    }

    /// <summary>Records, once, that money has been held for nearly the maximum (<c>MaxHoldDays</c>), and tells the admins. The money does not move.</summary>
    private async Task<bool> WarnAboutHoldAgeAsync(PaymentRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ServicePaymentNotice? notice = null;
        var warned = await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
                var now = Now();
                if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld, HoldWarnedAt: null }
                    || !HeldPaymentRules.IsMoneyHeld(payment.Status)
                    || !HeldPaymentRules.IsNearMaxHold(payment.CapturedAt, now, options.Value.MaxHoldDays, options.Value.HoldWarningDays))
                    return false;

                payment.HoldWarnedAt = now;
                payment.UpdatedAt = now;
                var heldDays = payment.CapturedAt is { } at ? (int)Math.Floor((now - at).TotalDays) : 0;
                notice = new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: $"held_for_{heldDays}_days");
                logger.LogError(
                    "Held payment {PaymentId} has been held for {Days} days, near the {MaxHoldDays} CasaZen means to hold money at most: release or refund it",
                    payment.Id, heldDays, options.Value.MaxHoldDays);
                return true;
            },
            cancellationToken);

        if (notice is not null)
            await CompleteAsync([notice], CancellationToken.None);

        return warned;
    }
}
