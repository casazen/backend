using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The life of a request after the take (SP-04): start, cancel, remind, propose another time and the batch accept. The rules are
// the ones of ServiceRequestStateMachine, the schedule rules the ones of the planner; every change is saved under the xmin check.
public partial class ServiceRequestService
{
    public async Task<ServiceRequest> StartAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

        await TransitionAsync(
            request,
            ServiceRequestStatus.InCorso,
            ServiceRequestErrorCodes.CannotStartMessageKey,
            r => r.StartedAt = Now(),
            cancellationToken);

        await notifier.NotifyHostAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> CancelAsSupplierAsync(
        Guid id,
        Guid supplierOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

        return await CancelAsync(request, ServiceRequestActorParty.Supplier, reason, cancellationToken);
    }

    public async Task<ServiceRequest> CancelAsHostAsync(
        Guid id,
        Guid hostOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);

        return await CancelAsync(request, ServiceRequestActorParty.Host, reason, cancellationToken);
    }

    /// <summary>
    /// <c>→ Annullato</c> by <paramref name="actor"/> (the host up to the work in progress, the supplier before it started),
    /// with the reason; the other party is told. The slot the request held is free again: a cancelled request is no occupancy.
    /// </summary>
    private async Task<ServiceRequest> CancelAsync(
        ServiceRequest request,
        ServiceRequestActorParty actor,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!ServiceRequestStateMachine.CanCancel(request.Status, actor))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotCancelMessageKey);
        }

        await TransitionAsync(
            request,
            ServiceRequestStatus.Annullato,
            ServiceRequestErrorCodes.CannotCancelMessageKey,
            r =>
            {
                r.CancelledAt = Now();
                r.CancelledBy = actor;
                r.CancellationReason = reason.Trim();
                r.ResponseDueAt = null;
                ClearProposal(r);
            },
            cancellationToken);

        if (actor == ServiceRequestActorParty.Host)
            await notifier.NotifySupplierCancelledAsync(request, cancellationToken);
        else
            await notifier.NotifyHostAsync(request, cancellationToken);

        return request;
    }

    public async Task<ServiceRequest> RemindAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);

        // A reminder asks for an answer: only a new request waits for one, and not one with a proposal the host has to answer.
        if (request.Status != ServiceRequestStatus.Richiesto || request.ProposedStartUtc is not null)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotRemindMessageKey);
        }

        var now = Now();
        var interval = options.Value.RemindIntervalHours;
        if (request.LastRemindedAt is { } last && now < last.AddHours(interval))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.RemindTooSoon, ServiceRequestErrorCodes.RemindTooSoonMessageKey, interval);
        }

        request.LastRemindedAt = now;
        request.UpdatedAt = now;
        await SaveAsync(request, "reminder", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the host reminded the supplier", request.Id);

        await notifier.NotifyReminderAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> ProposeTimeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        ProposeServiceRequestTimeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        if (request.Status != ServiceRequestStatus.Richiesto)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotProposeMessageKey);
        }

        var start = UtcDateTime.Normalize(command.StartUtc);
        var end = command.EndUtc is { } requestedEnd ? UtcDateTime.Normalize(requestedEnd) : (DateTime?)null;
        var schedule = await ResolveScheduleAsync(request, supplierOrgId, start, end, cancellationToken);
        var message = string.IsNullOrWhiteSpace(command.Message) ? null : command.Message.Trim();

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);
        await using (transaction)
        {
            // The proposed time is a time the supplier can do: checked like the time of a take, without the request itself.
            await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

            var now = Now();
            request.ProposedStartUtc = schedule.Start;
            request.ProposedEndUtc = schedule.End;
            request.ProposedAt = now;
            request.ProposedByUserId = userId;
            request.ProposalMessage = message;
            request.UpdatedAt = now;

            // A showcase request has no host who may take as long as it needs: the customer has Suppliers:Showcase:
            // ProposalResponseMinutes to answer (the answer arrives with SP-11), then the request lapses like one nobody
            // answered. The deadline of the supplier is spent: the supplier did answer.
            if (request.RentalContext == ServiceRequestRentalContext.Showcase)
                request.ResponseDueAt = now.AddMinutes(showcaseOptions.Value.ProposalResponseMinutes);

            await SaveAsync(request, "proposal", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the supplier proposed another time", request.Id);

        await notifier.NotifyTimeProposedAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> AcceptProposalAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);
        var (proposedStart, proposedEnd, proposedAt) = RequirePendingProposal(request);

        // The proposal is the supplier's offer to do the work at that time: accepting it takes the request on the supplier's
        // behalf. A supplier suspended meanwhile cannot take work.
        var supplierStatus = await ReadSupplierStatusAsync(request.SupplierOrgId, cancellationToken);
        if (supplierStatus != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        var minutes = (int)(proposedEnd - proposedStart).TotalMinutes;
        var service = request.ServiceListingId is { } listingId
            ? await catalog.FindForRequestAsync(request.SupplierOrgId, listingId, cancellationToken)
            : null;
        var schedule = new RequestSchedule(
            proposedStart,
            proposedEnd,
            new SupplierSlotQuery(minutes, service?.MinNoticeHours, service?.WeekdaysMask));
        var proposedBy = request.ProposedByUserId;

        // SP-15a: accepting the proposal is a take, so the request is paid the way the supplier can be paid right now.
        var paymentMode = await payments.ResolveModeAsync(request.SupplierOrgId, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(request.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            // Checked again under the lock: the slot may have been taken, or closed, since the supplier proposed it.
            await EnsureSlotFreeAsync(request.SupplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

            await TransitionAsync(
                request,
                ServiceRequestStatus.PresoInCarico,
                ServiceRequestErrorCodes.CannotTakeMessageKey,
                r =>
                {
                    r.PaymentMode = paymentMode;
                    r.ScheduledStartUtc = proposedStart;
                    r.ScheduledEndUtc = proposedEnd;
                    r.TakenAt = Now();
                    r.TakenByUserId = proposedBy;
                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        await notifier.NotifyProposalAnsweredAsync(request, accepted: true, proposedAt, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectProposalAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);
        var (_, _, proposedAt) = RequirePendingProposal(request);

        // The request stays new and waits for another answer from the supplier. A pending proposal keeps the
        // auto-cancel job away; if that deadline already passed while the host was deciding, start the window
        // again. Leaving the elapsed deadline would cancel the request as if nobody had answered.
        var now = Now();
        if (request.ResponseDueAt is { } due && due <= now)
            request.ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes);

        ClearProposal(request);
        request.UpdatedAt = now;
        await SaveAsync(request, "proposal rejected", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the host turned the proposed time down", request.Id);

        await notifier.NotifyProposalAnsweredAsync(request, accepted: false, proposedAt, cancellationToken);
        return request;
    }

    /// <summary>
    /// The proposal a new request is waiting for the host to answer; 422 <see cref="ServiceRequestErrorCodes.NoProposal"/> when the
    /// request has none (never made, answered, or the request moved on and dropped it).
    /// </summary>
    private static (DateTime Start, DateTime End, DateTime ProposedAt) RequirePendingProposal(ServiceRequest request)
    {
        if (request is { Status: ServiceRequestStatus.Richiesto, ProposedStartUtc: { } start, ProposedEndUtc: { } end, ProposedAt: { } proposedAt })
            return (start, end, proposedAt);

        throw new DomainRuleException(ServiceRequestErrorCodes.NoProposal, ServiceRequestErrorCodes.NoProposalMessageKey);
    }

    // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
    private async Task<SupplierStatus?> ReadSupplierStatusAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => (SupplierStatus?)sp.Status)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ServiceRequestBatchResult>> AcceptManyAsync(
        Guid supplierOrgId,
        string userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > ServiceRequestLimits.MaxBatchAccept)
            throw new ArgumentOutOfRangeException(nameof(ids), $"A batch accepts at most {ServiceRequestLimits.MaxBatchAccept} requests.");

        var results = new List<ServiceRequestBatchResult>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var taken = await TakeAsync(id, supplierOrgId, userId, command: null, cancellationToken);
                results.Add(new ServiceRequestBatchResult(id, Accepted: true, taken.Status, Code: null, MessageKey: null, MessageArgs: []));
            }
            catch (DomainException ex)
            {
                results.Add(Failed(id, ex.Code, ex.MessageKey, ex.MessageArgs));
            }
            catch (NotFoundException ex)
            {
                results.Add(Failed(
                    id,
                    ex.Code ?? ServiceRequestErrorCodes.NotFound,
                    ex.MessageKey ?? ServiceRequestErrorCodes.NotFoundMessageKey,
                    []));
            }
            catch (UnauthorizedAccessException)
            {
                // A request of another supplier answers like a missing one: the batch never tells that it exists.
                results.Add(Failed(id, ServiceRequestErrorCodes.NotFound, ServiceRequestErrorCodes.NotFoundMessageKey, []));
            }

            // A request that failed may still be tracked with the change that was refused: it must not be saved by the next one.
            if (!results[^1].Accepted)
                db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Supplier {SupplierOrgId}: batch accept, {Accepted} of {Total} requests taken",
            supplierOrgId, results.Count(r => r.Accepted), results.Count);
        return results;
    }

    private static ServiceRequestBatchResult Failed(Guid id, string code, string messageKey, IReadOnlyList<object> args) =>
        new(id, Accepted: false, Status: null, code, messageKey, args);
}
