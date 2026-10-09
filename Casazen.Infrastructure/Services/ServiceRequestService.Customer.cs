using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// What the customer of a supplier's public showcase does to its own request (SP-11): cancel it, move it to another time while the
// supplier has not answered, answer the time the supplier proposed. The state machine, the xmin check, the calendar lock and the slot
// planner are the ones of the supplier's and the host's actions; what is different is the party, the rules of ShowcaseBookingManagementRules
// and who is told (ShowcaseBookingNotifier). The caller (ShowcaseBookingManager) has proved that the customer knows the code and the
// e-mail address of the booking; here the request is looked for by its id and its supplier, and only a showcase request is found.
public partial class ServiceRequestService : IShowcaseRequestCustomerActions
{
    public async Task<ServiceRequest> CancelAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        // Checked before anything is read: a reason that is not valid is the same 422 for everybody who got this far.
        var text = ShowcaseBookingManagementRules.NormalizeReason(reason);

        ServiceRequest request;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            // Read after the lock: whoever held it before is seen, and a booking another door moved or cancelled a moment ago is judged as it is.
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);

            // A second call, or a retry after a lost answer: the customer has already cancelled it. Nothing happens again.
            if (request is { Status: ServiceRequestStatus.Annullato, CancelledBy: ServiceRequestActorParty.Customer })
                return request;

            var now = NowToTheMicrosecond();
            if (!ShowcaseBookingManagementRules.CanCancel(request.Status, request.ScheduledStartUtc, now))
                throw ShowcaseBookingManagementErrors.CancelRefused();

            await TransitionAsync(
                request,
                ServiceRequestStatus.Annullato,
                ServiceRequestErrorCodes.CannotCancelMessageKey,
                r =>
                {
                    r.CancelledAt = now;
                    r.CancelledBy = ServiceRequestActorParty.Customer;
                    r.CancellationReason = text ?? ServiceRequestCancellationReasons.CancelledByCustomer;
                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: cancelled by its customer", request.Id);

        await notifier.NotifyShowcaseCancelledByCustomerAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RescheduleAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime startUtc,
        CancellationToken cancellationToken = default)
    {
        var start = UtcDateTime.Normalize(startUtc);

        // The slots of the planner start on a whole minute: anything else is not one of them (the same field error as a booking).
        if (start.Ticks % TimeSpan.TicksPerMinute != 0)
            throw ShowcaseBookingErrors.InvalidFields([ShowcaseBookingFields.StartUtc]);

        ServiceRequest request;
        DateTime previousStart;
        bool proposalDropped;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
            var now = NowToTheMicrosecond();

            // Only a new request: once the supplier took it, the time is agreed. And only a request that has a time to move.
            if (request.Status != ServiceRequestStatus.Richiesto
                || request.ScheduledStartUtc is not { } currentStart
                || request.ScheduledEndUtc is not { } currentEnd)
            {
                throw ShowcaseBookingManagementErrors.RescheduleRefused();
            }

            await EnsureSupplierActiveForCustomerAsync(supplierOrgId, cancellationToken);

            // The service has to be published still: it gives the rules the planner applies (notice, days), and the page needs its slots.
            var service = request.ServiceListingId is { } listingId
                ? await catalog.FindForRequestAsync(supplierOrgId, listingId, cancellationToken)
                : null;
            if (service is not { IsRequestable: true })
                throw ShowcaseBookingManagementErrors.RescheduleRefused();

            // The time it already has: a retry after a lost answer, or a double click. Nothing is moved, nobody is told again.
            if (start == currentStart)
                return request;

            // A start no slot can have (the past, beyond the largest horizon, the ends of the calendar) is a slot that is not free,
            // answered before any date arithmetic runs on it.
            if (!ShowcaseBookingRules.IsWithinSlotWindow(start, now))
                throw SlotUnavailable();

            // The work keeps its length; the new start has to be one of the slots the planner offers for it, with the request itself
            // out of its own way, judged after the lock was taken.
            var minutes = (int)Math.Round((currentEnd - currentStart).TotalMinutes);
            var schedule = new RequestSchedule(
                start,
                start.AddMinutes(minutes),
                new SupplierSlotQuery(minutes, service.MinNoticeHours, service.WeekdaysMask));
            await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

            var settings = await agenda.GetBookingSettingsAsync(supplierOrgId, cancellationToken);

            previousStart = currentStart;
            proposalDropped = request.ProposedStartUtc is not null;
            request.ScheduledStartUtc = schedule.Start;
            request.ScheduledEndUtc = schedule.End;

            // The supplier has its whole time to answer again; a time it had proposed is moot (the customer chose another one).
            request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
            ClearProposal(request);
            request.UpdatedAt = now;

            await SaveAsync(request, "reschedule by the customer", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: its customer moved it to another time", request.Id);

        await notifier.NotifyShowcaseRescheduledAsync(request, previousStart, proposalDropped, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> AcceptProposalAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        ServiceRequest request;
        DateTime proposedAt;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
            var now = NowToTheMicrosecond();
            var (proposedStart, proposedEnd, madeAt) = RequireProposalToAnswer(request, now);

            // Accepting takes the request on the supplier's behalf: a supplier suspended meanwhile cannot take work.
            await EnsureSupplierActiveForCustomerAsync(supplierOrgId, cancellationToken);

            var service = request.ServiceListingId is { } listingId
                ? await catalog.FindForRequestAsync(supplierOrgId, listingId, cancellationToken)
                : null;
            var minutes = (int)Math.Round((proposedEnd - proposedStart).TotalMinutes);
            var schedule = new RequestSchedule(
                proposedStart,
                proposedEnd,
                new SupplierSlotQuery(minutes, service?.MinNoticeHours, service?.WeekdaysMask));

            // Checked again under the lock, like the host's acceptance: the slot may have been taken, or closed, since the supplier
            // proposed it. Then 409 and the proposal stays: the customer can still turn it down, or cancel.
            await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

            var proposedBy = request.ProposedByUserId;
            await TransitionAsync(
                request,
                ServiceRequestStatus.PresoInCarico,
                ServiceRequestErrorCodes.CannotTakeMessageKey,
                r =>
                {
                    r.ScheduledStartUtc = proposedStart;
                    r.ScheduledEndUtc = proposedEnd;
                    r.TakenAt = now;
                    r.TakenByUserId = proposedBy;
                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            proposedAt = madeAt;
        }

        logger.LogInformation("ServiceRequest {Id}: its customer accepted the time the supplier proposed", request.Id);

        await notifier.NotifyShowcaseProposalAnsweredAsync(request, accepted: true, proposedAt, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectProposalAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
        var now = NowToTheMicrosecond();
        var (_, _, proposedAt) = RequireProposalToAnswer(request, now);
        await EnsureSupplierActiveForCustomerAsync(supplierOrgId, cancellationToken);

        // The request goes back to being a new one at its own time: the deadline that stood on it was the customer's, and the supplier
        // has its whole time to answer again. Nothing is held or released: no lock, the check of the row version is enough.
        var settings = await agenda.GetBookingSettingsAsync(supplierOrgId, cancellationToken);
        ClearProposal(request);
        request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
        request.UpdatedAt = now;
        await SaveAsync(request, "proposal rejected by the customer", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: its customer turned the proposed time down", request.Id);

        await notifier.NotifyShowcaseProposalAnsweredAsync(request, accepted: false, proposedAt, cancellationToken);
        return request;
    }

    /// <summary>
    /// The proposal a new showcase request is waiting for its customer to answer, while there is still time to: 422
    /// <see cref="ShowcaseBookingManagementErrors.NoProposal"/> when there is none (never made, answered, or the request moved on),
    /// 422 <see cref="ShowcaseBookingManagementErrors.ProposalExpired"/> once its deadline (<c>ResponseDueAt</c>, which a proposal of a
    /// showcase request moves to the customer's time) has passed: the upkeep job cancels the request on its next run.
    /// </summary>
    private static (DateTime Start, DateTime End, DateTime ProposedAt) RequireProposalToAnswer(ServiceRequest request, DateTime now)
    {
        if (request is not { Status: ServiceRequestStatus.Richiesto, ProposedStartUtc: { } start, ProposedEndUtc: { } end, ProposedAt: { } proposedAt })
            throw ShowcaseBookingManagementErrors.ProposalMissing();

        if (request.ResponseDueAt is not { } answerBy || answerBy <= now)
            throw ShowcaseBookingManagementErrors.ProposalLapsed();

        return (start, end, proposedAt);
    }

    /// <summary>
    /// The showcase request <paramref name="id"/> of the supplier org, tracked. 404 <see cref="ShowcaseBookingManagementErrors.NotFound"/>
    /// for anything else: a request that does not exist, one of a host, one of another supplier.
    /// </summary>
    private async Task<ServiceRequest> GetShowcaseRequestOrThrow(Guid id, Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);
        if (request is null
            || request.RentalContext != ServiceRequestRentalContext.Showcase
            || request.SupplierOrgId != supplierOrgId)
        {
            throw ShowcaseBookingManagementErrors.BookingNotFound();
        }

        return request;
    }

    /// <summary>422 <c>supplier_booking_supplier_unavailable</c> unless the supplier is active: a suspended one answers nothing.</summary>
    private async Task EnsureSupplierActiveForCustomerAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        if (await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) != SupplierStatus.Active)
            throw ShowcaseBookingErrors.InactiveSupplier();
    }

    private static DomainConflictException SlotUnavailable() =>
        new(ServiceRequestErrorCodes.SlotUnavailable, ServiceRequestErrorCodes.SlotUnavailableMessageKey);

    /// <summary>Now at the precision the database keeps (the microsecond): what is written is what is read back.</summary>
    private DateTime NowToTheMicrosecond() => UtcDateTime.TruncateToMicroseconds(Now());
}
