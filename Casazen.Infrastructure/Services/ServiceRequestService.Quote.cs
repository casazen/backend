using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The quote with the customer's approval (SP-04b): the supplier answers a new request with an offer (lines, total, optionally another time),
// the customer — the host, or the private customer of a showcase request — accepts or turns it down. Like the proposed time it is an offer on a
// request still waiting for its answer (Richiesto): it holds no slot, one offer waits at a time, and accepting it is a take made on the
// supplier's behalf, checked again under the supplier's calendar lock. The customer's side of a showcase request is in ServiceRequestService.Customer.cs.
public partial class ServiceRequestService
{
    public async Task<ServiceRequest> SendQuoteAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        SendServiceRequestQuoteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

        // The quote answers a request nobody answered yet: not one the supplier took, refused, or that was cancelled.
        if (request.Status != ServiceRequestStatus.Richiesto)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotQuoteMessageKey);
        }

        // SP-15c-a: a request whose card is blocked has its price: the total the customer saw is what is blocked, and a quote would be another
        // price (R7: a different price needs a new offer, not an answer of the supplier). Refused before anything else is read.
        if (IsPaidInsideCasaZen(request))
            throw new DomainRuleException(ServicePaymentErrors.AmountLocked, ServicePaymentErrors.AmountLockedMessageKey);

        // SP-BKP: a backup answers the request as it is or refuses; the host would be answering a supplier it did not choose.
        if (request.GroupRole == ServiceRequestGroupRole.Backup)
        {
            throw new DomainRuleException(
                ServiceRequestGroupErrorCodes.BackupCannotQuote, ServiceRequestGroupErrorCodes.BackupCannotQuoteMessageKey);
        }

        var (lines, total) = ServiceRequestQuoteRules.Normalize(command.Lines);
        var note = ServiceRequestQuoteRules.NormalizeNote(command.Note);
        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);

        // The quote leans on the time the request has (every request has one). Another time is optional and is judged like the time of a
        // proposal: with the planner, under the lock, and without holding the slot.
        RequestSchedule? other = null;
        if (command.ScheduledStartUtc is { } requestedStart)
        {
            var start = UtcDateTime.Normalize(requestedStart);
            var end = command.ScheduledEndUtc is { } requestedEnd ? UtcDateTime.Normalize(requestedEnd) : (DateTime?)null;
            other = await ResolveScheduleAsync(request, supplierOrgId, start, end, cancellationToken);
        }
        else if (command.ScheduledEndUtc is not null)
        {
            // An end without a start says nothing.
            throw TimeInvalid();
        }

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);
        await using (transaction)
        {
            if (other is not null)
            {
                await EnsureSlotFreeAsync(supplierOrgId, other, exceptRequestId: request.Id, cancellationToken);
                await EnsureNoOverlappingCommitmentAsync(supplierOrgId, request.Id, other.Start, other.End, cancellationToken);
            }

            var now = NowToTheMicrosecond();
            request.QuoteSentAt = now;
            request.QuoteSentByUserId = userId;
            request.QuoteLinesJson = ServiceRequestJson.Serialize(lines);
            request.QuoteTotalCents = total;
            request.QuoteNote = note;
            request.QuoteScheduledStartUtc = other?.Start;
            request.QuoteScheduledEndUtc = other?.End;
            AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Sent, now, total);

            // One offer at a time: the quote takes the place of a proposed time.
            ClearProposal(request);

            // A showcase request has no host who may take as long as it needs: its customer has the same time to answer as for a proposed
            // time (Suppliers:Showcase:ProposalResponseMinutes), then the request lapses like one nobody answered. The supplier did answer.
            // A host has no deadline; the one of the request is the supplier's own, which the auto-cancel job leaves alone while a quote waits.
            if (request.RentalContext == ServiceRequestRentalContext.Showcase)
                request.ResponseDueAt = now.AddMinutes(showcaseOptions.Value.ProposalResponseMinutes);

            request.UpdatedAt = now;
            await SaveAsync(request, "quote", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the supplier sent a quote", request.Id);

        // A quote is an answer too (the request then waits for the customer, not for the supplier).
        await RecordReplyAsync(request, dueBeforeAnswer, asBackup: false, cancellationToken);
        await notifier.NotifyQuoteSentAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> WithdrawQuoteAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        var quote = RequirePendingQuote(request, quoteSentAt: null);

        // The request is "to answer" again: the supplier answers it within its own time, as after an answer of the customer.
        var now = NowToTheMicrosecond();
        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Withdrawn, now, quote.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "quote withdrawn", cancellationToken);

        // SP-BKP: the primary is "to answer" again, and the window of its group starts again the same way if it passed while the quote waited.
        if (request.GroupId is { } groupId)
            await ReopenGroupWindowAsync(groupId, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the supplier took its quote back", request.Id);
        return request;
    }

    public async Task<ServiceRequest> AcceptQuoteAsync(
        Guid id,
        Guid hostOrgId,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);
        var quote = RequirePendingQuote(request, quoteSentAt);

        // The quote is the supplier's offer to do the work at that price: accepting it takes the request on the supplier's behalf. A
        // supplier suspended meanwhile cannot take work.
        var supplierStatus = await ReadSupplierStatusAsync(request.SupplierOrgId, cancellationToken);
        if (supplierStatus != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        // SP-15a: accepting the quote is a take, so the request is paid the way the supplier can be paid right now.
        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(request.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            await TakeOnQuoteAsync(request, quote, paymentMode, NowToTheMicrosecond(), cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the host accepted the quote", request.Id);

        await notifier.NotifyQuoteAnsweredAsync(request, accepted: true, quote, cancellationToken);

        // SP-BKP: accepting the primary's quote is the take of its job: the backups that still wait are closed.
        await ResolveGroupAfterChangeAsync(request.GroupId, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectQuoteAsync(
        Guid id,
        Guid hostOrgId,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);
        var quote = RequirePendingQuote(request, quoteSentAt);

        // The request stays new and waits for another answer from the supplier. The deadline it had may have passed while the host was
        // deciding: it starts again, or the job would cancel the request as if nobody had answered.
        var now = NowToTheMicrosecond();
        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Rejected, now, quote.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "quote rejected", cancellationToken);

        // SP-BKP: the window of a group starts again the same way, for the primary and the backups together.
        if (request.GroupId is { } groupId)
            await ReopenGroupWindowAsync(groupId, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the host turned the quote down", request.Id);

        await notifier.NotifyQuoteAnsweredAsync(request, accepted: false, quote, cancellationToken);
        return request;
    }

    // ─── Shared by the host's and the customer's acceptance ───────────────────────────────────────────────────────────

    /// <summary>
    /// The take that accepting <paramref name="quote"/> is, with the supplier's calendar lock held by the caller: the time of the quote
    /// (or, with none of its own, the one the request has) is checked again and must not overlap a job the supplier already accepted; then
    /// <c>Richiesto → PresoInCarico</c> by the supplier member who sent the quote, with the total as the agreed price and the lines as the
    /// price lines. A time that is gone is 409 and nothing is saved, so the quote stays: the customer can turn it down, or cancel.
    /// </summary>
    private async Task TakeOnQuoteAsync(
        ServiceRequest request,
        ServiceRequestQuote quote,
        ServiceRequestPaymentMode paymentMode,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var offeredTime = quote is { ScheduledStartUtc: not null, ScheduledEndUtc: not null };
        if (offeredTime)
        {
            // Another time than the request's own: it was an offer, not a reservation, so the planner judges it again now.
            var service = request.ServiceListingId is { } listingId
                ? await catalog.FindForRequestAsync(request.SupplierOrgId, listingId, cancellationToken)
                : null;
            var minutes = (int)Math.Round((quote.ScheduledEndUtc!.Value - quote.ScheduledStartUtc!.Value).TotalMinutes);
            var schedule = new RequestSchedule(
                quote.ScheduledStartUtc.Value,
                quote.ScheduledEndUtc.Value,
                new SupplierSlotQuery(minutes, service?.MinNoticeHours, service?.WeekdaysMask));
            await EnsureSlotFreeAsync(request.SupplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);
        }

        await EnsureNoOverlappingCommitmentAsync(
            request.SupplierOrgId,
            request.Id,
            quote.ScheduledStartUtc ?? request.ScheduledStartUtc,
            quote.ScheduledEndUtc ?? request.ScheduledEndUtc,
            cancellationToken);

        var takenBy = request.QuoteSentByUserId;
        await TransitionAsync(
            request,
            ServiceRequestStatus.PresoInCarico,
            ServiceRequestErrorCodes.CannotTakeMessageKey,
            r =>
            {
                r.PaymentMode = paymentMode;
                if (offeredTime)
                {
                    r.ScheduledStartUtc = quote.ScheduledStartUtc;
                    r.ScheduledEndUtc = quote.ScheduledEndUtc;
                }

                r.TakenAt = now;
                r.TakenByUserId = takenBy;

                // The total is the price agreed (the reference of decision D7 for the final amount), and the lines say what it is made of.
                r.QuotedAmountCents = quote.TotalCents;
                r.PriceLinesJson = ServiceRequestJson.Serialize(ServiceRequestQuoteRules.AsPriceLines(quote.Lines));
                AppendQuoteEvent(r, ServiceRequestQuoteEventKinds.Accepted, now, quote.TotalCents);
                r.ResponseDueAt = null;
                ClearProposal(r);
            },
            cancellationToken);
    }

    /// <summary>
    /// The quote that waits on the request, for the host or the supplier: 422 <see cref="ServiceRequestErrorCodes.NoQuote"/> when none does.
    /// With <paramref name="quoteSentAt"/> (what the customer read) it has to be that very quote: another one means the supplier replaced it
    /// meanwhile, 409 <see cref="ServiceRequestErrorCodes.QuoteChanged"/>, and nothing is answered.
    /// </summary>
    private static ServiceRequestQuote RequirePendingQuote(ServiceRequest request, DateTime? quoteSentAt)
    {
        var quote = ServiceRequestQuoteRules.PendingOf(request)
            ?? throw new DomainRuleException(ServiceRequestErrorCodes.NoQuote, ServiceRequestErrorCodes.NoQuoteMessageKey);

        if (quoteSentAt is { } asked && !ServiceRequestQuoteRules.IsSameVersion(quote.SentAt, asked))
            throw new DomainConflictException(ServiceRequestErrorCodes.QuoteChanged, ServiceRequestErrorCodes.QuoteChangedMessageKey);

        return quote;
    }

    /// <summary>
    /// The supplier has its time to answer again after its quote went (withdrawn, or turned down): a showcase request gives it the whole
    /// <c>RespondWithinMinutes</c> of its settings, as after any answer of the customer; a host's request keeps the deadline it had unless
    /// that has passed meanwhile, and then the window starts again (the quote kept the auto-cancel job away, but a deadline in the past would
    /// cancel the request as soon as the quote is gone).
    /// </summary>
    private async Task RestartSupplierDeadlineAsync(ServiceRequest request, DateTime now, CancellationToken cancellationToken)
    {
        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            var settings = await agenda.GetBookingSettingsAsync(request.SupplierOrgId, cancellationToken);
            request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
        }
        else if (request.GroupId is null && request.ResponseDueAt is { } due && due <= now)
        {
            // A request of a group has the deadline of the group's window (ReopenGroupWindowAsync starts it again).
            request.ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes);
        }
    }

    /// <summary>The quote is dropped when the request moves on (taken, refused, cancelled) or when the supplier replaces it with a proposed time.</summary>
    internal static void ClearQuote(ServiceRequest request)
    {
        request.QuoteSentAt = null;
        request.QuoteSentByUserId = null;
        request.QuoteLinesJson = "[]";
        request.QuoteTotalCents = null;
        request.QuoteNote = null;
        request.QuoteScheduledStartUtc = null;
        request.QuoteScheduledEndUtc = null;
    }

    /// <summary>Adds an event to the trace of the quotes of the request (the last ones are kept).</summary>
    private static void AppendQuoteEvent(ServiceRequest request, string kind, DateTime at, int? totalCents) =>
        request.QuoteEventsJson = ServiceRequestJson.Serialize(
            ServiceRequestQuoteRules.Append(
                ServiceRequestJson.ReadQuoteEvents(request.QuoteEventsJson),
                new ServiceRequestQuoteEvent(kind, at, totalCents)));
}
