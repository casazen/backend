using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestAutoCancelService"/>
/// <remarks>
/// <para>Two runs share the code that cancels one request: the one of the hosts' requests (SP-04,
/// <see cref="CancelUnansweredAsync"/>, behind the flag <c>SupplierRequestAutoCancel</c>, a session lock of its own) and the one
/// of the requests from the suppliers' public showcases (SP-10, <see cref="CancelUnansweredShowcaseAsync"/>, run by
/// <see cref="ServiceRequestExpiryService"/> under its lock). They read disjoint sets (the context of the request decides), so
/// neither cancels what the other owns, and the cancellation is the same: <c>Richiesto → Annullato</c> by CasaZen with the reason
/// <c>NoResponse</c> (or, for a showcase request whose customer did not answer the time the supplier proposed,
/// <c>ProposalNotAnswered</c>: SP-11), the other parties told by <see cref="ServiceRequestNotifier"/> (the host for a host's
/// request, the customer for a showcase one).</para>
/// <para><b>Money (SP-15c-a).</b> A showcase request paid inside CasaZen that is still new when the block on its customer's card is gone (it
/// lapsed, or was refused at the take) can never be accepted: <see cref="UnpayableShowcaseIdsOf"/> finds it, whatever its deadline, and it is
/// cancelled as if nobody had answered — the answer time of such a request never reaches past the block, so this is the safety net, not the
/// rule. Every showcase request this run cancels releases the block on the card it may still have (<see cref="ISupplierFundsService.SettleClosedRequestAsync"/>,
/// which never throws; the funds job settles it if this cannot).</para>
/// </remarks>
public sealed class ServiceRequestAutoCancelService(
    AppDbContext db,
    ServiceRequestNotifier notifier,
    IFeatureFlags featureFlags,
    ILogger<ServiceRequestAutoCancelService> logger,
    TimeProvider? timeProvider = null,
    ISupplierFundsService? funds = null,
    ISupplierReliabilityService? reliability = null,
    IOptions<ServiceRequestOptions>? requestOptions = null) : IServiceRequestAutoCancelService
{
    internal const string RunLockKey = "service-request-auto-cancel";

    /// <summary>
    /// Requests one run handles at most: the first run after the flag is turned on may find many overdue requests, and a run
    /// that stays short is better than one that holds the lock for minutes. The next run (10 minutes later) goes on.
    /// </summary>
    public const int MaxPerRun = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestAutoCancelRun> CancelUnansweredAsync(CancellationToken cancellationToken = default)
    {
        // Also checked here and not only when the job is scheduled: a run triggered by hand, or by a schedule an earlier deploy
        // left behind, does nothing while the flag is off.
        if (!featureFlags.IsEnabled(FeatureFlags.SupplierRequestAutoCancel))
            return ServiceRequestAutoCancelRun.FlagOff;

        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestAutoCancelRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request auto-cancel run skipped: another run is in progress");
            return ServiceRequestAutoCancelRun.LockTaken;
        }

        return await CancelDueAsync(showcase: false, cancellationToken);
    }

    public Task<ServiceRequestAutoCancelRun> CancelUnansweredShowcaseAsync(CancellationToken cancellationToken = default) =>
        CancelDueAsync(showcase: true, cancellationToken);

    private async Task<ServiceRequestAutoCancelRun> CancelDueAsync(bool showcase, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var dueIds = await (showcase ? DueShowcaseIdsOf(db, now) : DueIdsOf(db, now)).ToListAsync(cancellationToken);

        // SP-15c-a: the new requests paid inside CasaZen that lost the block on their customer's card are due whatever their deadline says.
        var unpayable = showcase ? await UnpayableShowcaseIdsOf(db).ToListAsync(cancellationToken) : [];
        var unpayableIds = unpayable.ToHashSet();
        dueIds = dueIds.Concat(unpayable.Where(id => !dueIds.Contains(id))).ToList();
        if (dueIds.Count == 0)
            return ServiceRequestAutoCancelRun.Empty;

        int cancelled = 0, conflicts = 0, failed = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await CancelAsync(id, now, showcase, unpayableIds.Contains(id), cancellationToken))
            {
                case Outcome.Cancelled:
                    cancelled++;
                    break;
                case Outcome.Conflict:
                    conflicts++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        logger.LogInformation(
            "Service request auto-cancel ({Scope}): {Cancelled} cancelled, {Conflicts} changed under the run, {Failed} failed",
            showcase ? "showcase" : "hosts", cancelled, conflicts, failed);
        return new ServiceRequestAutoCancelRun(Disabled: false, Skipped: false, cancelled, conflicts, failed);
    }

    /// <summary>
    /// The new requests of the hosts whose deadline has passed at <paramref name="nowUtc"/> and that have no time proposed and no quote
    /// sent (SP-04b: the supplier answered, the host has no deadline), the oldest deadline first, at most <see cref="MaxPerRun"/>.
    /// ServiceRequest has two parties and no tenant filter (TN-2 allow-list): the job works across every org, so the filters of the host
    /// are off and the predicate is the state of the request itself. A request of the public showcase is not one of them (it is
    /// <see cref="DueShowcaseIdsOf"/>), nor is one that belongs to a group of backup suppliers (SP-BKP): the primary and the backups lapse,
    /// or are given the job, by the logic of the group (<c>IServiceRequestService.ResolveGroupAsync</c>, run by the job
    /// <c>service-request-groups</c>), never one by one here.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> DueIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext != ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.ResponseDueAt != null
                        && r.ResponseDueAt <= nowUtc
                        && r.ProposedStartUtc == null
                        && r.QuoteSentAt == null
                        && r.GroupId == null)
            .OrderBy(r => r.ResponseDueAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxPerRun);

    /// <summary>
    /// The new requests of the public showcases whose deadline has passed at <paramref name="nowUtc"/>, the oldest deadline
    /// first, at most <see cref="MaxPerRun"/>. A pending proposal of another time does not exempt them: the proposal moved the
    /// deadline to the instant the customer has to answer by, so the one that is due is the one nobody answered.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> DueShowcaseIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.ResponseDueAt != null
                        && r.ResponseDueAt <= nowUtc)
            .OrderBy(r => r.ResponseDueAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxPerRun);

    /// <summary>
    /// The new requests of the public showcases that are paid inside CasaZen (<c>PaymentMode</c> online: their customer's card was blocked when
    /// they were born) and have no payment left that is not canceled: the block is gone, so the supplier can never accept them (SP-15c-a). At most
    /// <see cref="MaxPerRun"/>. The predicate on the payments is the request id, as everywhere the table is read.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> UnpayableShowcaseIdsOf(AppDbContext db) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.PaymentMode == ServiceRequestPaymentMode.Online
                        && !db.ServiceRequestPayments
                            .AsNoTracking()
                            .Where(p => p.ServiceRequestId == r.Id && p.Status != ServicePaymentStatus.Canceled)
                            .Any())
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxPerRun);

    private enum Outcome
    {
        Cancelled,
        Conflict,
        Failed,
    }

    /// <summary>
    /// When the request began to wait for the supplier's answer: its creation, or the start of the last window when the deadline was
    /// restarted (a proposal or a quote the host turned down), that is <c>Suppliers:ServiceRequests:HostResponseMinutes</c> before the
    /// deadline. The supplier is judged on the time it really had.
    /// </summary>
    private DateTime WaitStartOf(ServiceRequest request, DateTime dueUtc)
    {
        // A request on measure from a showcase waits from the moment it was made: its clock is never restarted (SP-10c).
        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
            return request.CreatedAt;

        var window = (requestOptions?.Value ?? new ServiceRequestOptions()).HostResponseMinutes;
        var start = dueUtc.AddMinutes(-window);
        return start > request.CreatedAt ? start : request.CreatedAt;
    }

    private async Task<Outcome> CancelAsync(Guid id, DateTime now, bool showcase, bool unpayable, CancellationToken cancellationToken)
    {
        // One request at a time, read again: what the run listed a moment ago may have been answered since.
        db.ChangeTracker.Clear();
        try
        {
            // IgnoreQueryFilters: see DueIdsOf. The property (none for a showcase request) and the supplier org are read for the
            // notifications.
            var request = await db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            // A request that lost the block on its customer's card (SP-15c-a) is due whatever its deadline says; it must still be a new
            // showcase request that is paid inside CasaZen, as it was when it was listed.
            var stillUnpayable = unpayable
                                 && request is { Status: ServiceRequestStatus.Richiesto, PaymentMode: ServiceRequestPaymentMode.Online };
            if (request is null
                || (request.RentalContext == ServiceRequestRentalContext.Showcase) != showcase
                || request.GroupId is not null
                || (!stillUnpayable && (request.ResponseDueAt is not { } due || due > now))
                || (!showcase && (request.ProposedStartUtc is not null || request.QuoteSentAt is not null))
                || !ServiceRequestStateMachine.CanCancel(request.Status, ServiceRequestActorParty.System))
            {
                return Outcome.Conflict;
            }

            // The deadline the request had, before it is cleared: the ranking (SP-REL) judges the supplier against it. A request of the hosts
            // always has one (the run lists only those); a showcase request that lost the block on its card (SP-15c-a) may have none.
            var deadline = request.ResponseDueAt;

            // A showcase request can lapse with a time proposed, or a quote sent, that its customer never answered (SP-11 and SP-04b give the
            // customer the way to answer): then it is the customer who did not answer, not the supplier, and the reason says so — nobody is
            // told the other one stayed silent (decision D24). A host's request with a proposal or a quote is never picked by this job.
            var customerLetTheQuoteLapse = showcase && request.QuoteSentAt is not null;
            var customerLetTheProposalLapse = showcase && request.ProposedStartUtc is not null;

            request.Status = ServiceRequestStatus.Annullato;
            request.CancelledAt = now;
            request.CancelledBy = ServiceRequestActorParty.System;
            request.CancellationReason = customerLetTheQuoteLapse
                ? ServiceRequestCancellationReasons.QuoteNotAnswered
                : customerLetTheProposalLapse
                    ? ServiceRequestCancellationReasons.ProposalNotAnswered
                    : ServiceRequestCancellationReasons.NoResponse;
            request.ResponseDueAt = null;

            // A cancelled request has no proposal and no quote waiting.
            ServiceRequestService.ClearProposal(request);
            ServiceRequestService.ClearQuote(request);
            request.UpdatedAt = now;

            // Saved only if the request is as it was read (xmin): a supplier that took it meanwhile wins.
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("ServiceRequest {Id}: Richiesto -> Annullato, no answer before the deadline", id);

            // SP-REL: a request of a host that nobody answered costs the supplier points, unless it could not have answered (paused, on
            // time off, outside its working hours while the request waited): then it loses none and is not told it did. The bookings
            // from a showcase are not part of the ranking, except the requests on measure (SP-10c): their hour is the rule of the platform
            // (Suppliers:Showcase:InquiryResponseMinutes), not the supplier's own setting, and the missed answer is written as the same event
            // (NoReplyInTime). A request the customer let lapse (a proposal or an offer it did not answer) is never the supplier's silence.
            // What the supplier is told is decided by the winner, after the save.
            var supplierStayedSilent = request.CancellationReason == ServiceRequestCancellationReasons.NoResponse;
            var countsForRanking = !showcase || request.Kind == ServiceRequestKind.Custom;
            var lostPoints = countsForRanking
                && supplierStayedSilent
                && reliability is not null
                && deadline is { } dueAt
                && await reliability.RecordNoReplyAsync(
                    request.SupplierOrgId, request.Id, WaitStartOf(request, dueAt), dueAt, asBackup: false, cancellationToken);

            // After the save, by the winner only: the host (or the customer of a showcase request) is told that nobody answered,
            // the supplier that it lost the request (and, when it did, the points).
            await notifier.NotifyHostAsync(request, cancellationToken);
            await notifier.NotifySupplierCancelledAsync(request, cancellationToken, lostPoints);

            // SP-15c-a: the block on the customer's card goes with the request. Never throws: the request is closed and stays so, and the
            // funds job releases the block on its next run if this cannot.
            if (showcase && funds is not null)
                await funds.SettleClosedRequestAsync(id, cancellationToken);

            return Outcome.Cancelled;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("ServiceRequest {Id}: changed while the auto-cancel run was cancelling it, left as it is", id);
            return Outcome.Conflict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ServiceRequest {Id} could not be cancelled by the auto-cancel run (retried by the next run)", id);
            return Outcome.Failed;
        }
    }
}
