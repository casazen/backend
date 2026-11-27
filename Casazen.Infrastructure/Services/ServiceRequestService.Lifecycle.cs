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

        var wasWaitingBackup = request.GroupRole == ServiceRequestGroupRole.Backup;

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
        else if (!wasWaitingBackup)
            await notifier.NotifyHostAsync(request, cancellationToken);

        await SettleClosedMoneyAsync(request, cancellationToken);

        // SP-BKP: the host's cancellation closes the backups; a supplier that gives up its request (the primary, or a backup) changes who is
        // in the race. The group is looked at right away.
        await ResolveGroupAfterChangeAsync(request.GroupId, cancellationToken);

        return request;
    }

    public async Task<ServiceRequest> RemindAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);

        if (request.Status != ServiceRequestStatus.Richiesto || request.ProposedStartUtc is not null || request.QuoteSentAt is not null)
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

    public async Task<ServiceRequest> RescheduleAsHostAsync(
        Guid id,
        Guid hostOrgId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);

        if (request is not { Status: ServiceRequestStatus.Richiesto, GroupId: null, ProposedStartUtc: null, QuoteSentAt: null })
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotRescheduleMessageKey);
        }

        var start = UtcDateTime.Normalize(startUtc);
        var end = UtcDateTime.Normalize(endUtc);
        var length = end - start;
        if (length <= TimeSpan.Zero
            || length.Ticks % TimeSpan.TicksPerMinute != 0
            || length.TotalMinutes is < SupplierServiceCatalogLimits.MinDurationMinutes or > SupplierServiceCatalogLimits.MaxDurationMinutes)
        {
            throw TimeInvalid();
        }

        await using (var transaction = await LockSupplierCalendarAsync(request.SupplierOrgId, cancellationToken))
        {
            await EnsureNoOverlappingCommitmentAsync(request.SupplierOrgId, request.Id, start, end, cancellationToken);

            request.ScheduledStartUtc = start;
            request.ScheduledEndUtc = end;
            request.UpdatedAt = Now();
            await SaveAsync(request, "reschedule", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the host moved it to another time", request.Id);
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
        EnsureNotOnMeasure(request);
        if (request.Status != ServiceRequestStatus.Richiesto)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotProposeMessageKey);
        }

        if (request.GroupRole == ServiceRequestGroupRole.Backup)
        {
            throw new DomainRuleException(
                ServiceRequestGroupErrorCodes.BackupCannotPropose, ServiceRequestGroupErrorCodes.BackupCannotProposeMessageKey);
        }

        var start = UtcDateTime.Normalize(command.StartUtc);
        var end = command.EndUtc is { } requestedEnd ? UtcDateTime.Normalize(requestedEnd) : (DateTime?)null;
        var schedule = await ResolveScheduleAsync(request, supplierOrgId, start, end, cancellationToken);
        var message = string.IsNullOrWhiteSpace(command.Message) ? null : command.Message.Trim();
        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);
        await using (transaction)
        {
            await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

            var now = Now();
            request.ProposedStartUtc = schedule.Start;
            request.ProposedEndUtc = schedule.End;
            request.ProposedAt = now;
            request.ProposedByUserId = userId;
            request.ProposalMessage = message;
            request.UpdatedAt = now;

            if (request.QuoteSentAt is not null)
            {
                AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Withdrawn, UtcDateTime.TruncateToMicroseconds(now), request.QuoteTotalCents);
                ClearQuote(request);
            }

            if (request.RentalContext == ServiceRequestRentalContext.Showcase)
                request.ResponseDueAt = now.AddMinutes(showcaseOptions.Value.ProposalResponseMinutes);

            await SaveAsync(request, "proposal", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the supplier proposed another time", request.Id);

        await RecordReplyAsync(request, dueBeforeAnswer, asBackup: false, cancellationToken);
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

        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(request.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            // Checked again under the lock: the slot may have been taken, or closed, since the supplier proposed it.
            await EnsureSlotFreeAsync(request.SupplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);
            await EnsureNoOverlappingCommitmentAsync(
                request.SupplierOrgId, request.Id, proposedStart, proposedEnd, cancellationToken);

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

        await ResolveGroupAfterChangeAsync(request.GroupId, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectProposalAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);
        var (_, _, proposedAt) = RequirePendingProposal(request);

        var now = Now();
        if (request.GroupId is null && request.ResponseDueAt is { } due && due <= now)
            request.ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes);

        ClearProposal(request);
        request.UpdatedAt = now;
        await SaveAsync(request, "proposal rejected", cancellationToken);

        if (request.GroupId is { } groupId)
            await ReopenGroupWindowAsync(groupId, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the host turned the proposed time down", request.Id);

        await notifier.NotifyProposalAnsweredAsync(request, accepted: false, proposedAt, cancellationToken);
        return request;
    }

    private static (DateTime Start, DateTime End, DateTime ProposedAt) RequirePendingProposal(ServiceRequest request)
    {
        if (request is { Status: ServiceRequestStatus.Richiesto, ProposedStartUtc: { } start, ProposedEndUtc: { } end, ProposedAt: { } proposedAt })
            return (start, end, proposedAt);

        throw new DomainRuleException(ServiceRequestErrorCodes.NoProposal, ServiceRequestErrorCodes.NoProposalMessageKey);
    }

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
                results.Add(Failed(id, ServiceRequestErrorCodes.NotFound, ServiceRequestErrorCodes.NotFoundMessageKey, []));
            }

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
