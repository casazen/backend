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

        EnsureNotOnMeasure(request);

        if (request.Status != ServiceRequestStatus.Richiesto)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotQuoteMessageKey);
        }

        if (IsPaidInsideCasaZen(request))
            throw new DomainRuleException(ServicePaymentErrors.AmountLocked, ServicePaymentErrors.AmountLockedMessageKey);

        if (request.GroupRole == ServiceRequestGroupRole.Backup)
        {
            throw new DomainRuleException(
                ServiceRequestGroupErrorCodes.BackupCannotQuote, ServiceRequestGroupErrorCodes.BackupCannotQuoteMessageKey);
        }

        var (lines, total) = ServiceRequestQuoteRules.Normalize(command.Lines);
        var note = ServiceRequestQuoteRules.NormalizeNote(command.Note);
        return await PublishQuoteAsync(
            request, supplierOrgId, userId, lines, total, note, command.ScheduledStartUtc, command.ScheduledEndUtc, cancellationToken);
    }

    private async Task<ServiceRequest> PublishQuoteAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        string userId,
        IReadOnlyList<ServiceRequestQuoteLine> lines,
        int total,
        string? note,
        DateTime? scheduledStartUtc,
        DateTime? scheduledEndUtc,
        CancellationToken cancellationToken)
    {
        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);

        RequestSchedule? other = null;
        if (scheduledStartUtc is { } requestedStart)
        {
            var start = UtcDateTime.Normalize(requestedStart);
            var end = scheduledEndUtc is { } requestedEnd ? UtcDateTime.Normalize(requestedEnd) : (DateTime?)null;
            other = await ResolveScheduleAsync(request, supplierOrgId, start, end, cancellationToken);
        }
        else if (scheduledEndUtc is not null)
        {
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

            ClearProposal(request);

            if (request.RentalContext == ServiceRequestRentalContext.Showcase)
                request.ResponseDueAt = now.AddMinutes(showcaseOptions.Value.ProposalResponseMinutes);

            request.UpdatedAt = now;
            await SaveAsync(request, "quote", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the supplier sent a quote", request.Id);

        if (request.Kind == ServiceRequestKind.Custom)
            await ReleaseOfferPaymentAsync(request.Id, CancellationToken.None);

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

        var now = NowToTheMicrosecond();
        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Withdrawn, now, quote.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "quote withdrawn", cancellationToken);

        if (request.GroupId is { } groupId)
            await ReopenGroupWindowAsync(groupId, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the supplier took its quote back", request.Id);

        if (request.Kind == ServiceRequestKind.Custom)
            await ReleaseOfferPaymentAsync(request.Id, CancellationToken.None);

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

        var supplierStatus = await ReadSupplierStatusAsync(request.SupplierOrgId, cancellationToken);
        if (supplierStatus != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(request.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            await TakeOnQuoteAsync(request, quote, paymentMode, NowToTheMicrosecond(), cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: the host accepted the quote", request.Id);

        await notifier.NotifyQuoteAnsweredAsync(request, accepted: true, quote, cancellationToken);

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

        var now = NowToTheMicrosecond();
        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Rejected, now, quote.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "quote rejected", cancellationToken);

        if (request.GroupId is { } groupId)
            await ReopenGroupWindowAsync(groupId, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the host turned the quote down", request.Id);

        await notifier.NotifyQuoteAnsweredAsync(request, accepted: false, quote, cancellationToken);
        return request;
    }

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
        CancellationToken cancellationToken,
        Func<Task>? beforeTake = null)
    {
        var offeredTime = quote is { ScheduledStartUtc: not null, ScheduledEndUtc: not null };
        await EnsureQuoteTimeStillFreeAsync(request, quote, cancellationToken);

        if (beforeTake is not null)
            await beforeTake();

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

                r.QuotedAmountCents = quote.TotalCents;
                r.PriceLinesJson = ServiceRequestJson.Serialize(ServiceRequestQuoteRules.AsPriceLines(quote.Lines));
                AppendQuoteEvent(r, ServiceRequestQuoteEventKinds.Accepted, now, quote.TotalCents);
                r.ResponseDueAt = null;
                ClearProposal(r);
            },
            cancellationToken);
    }

    /// <summary>
    /// The time a quote leans on is still the supplier's to give, with its calendar lock held by the caller: the time the quote offers (or, with none
    /// of its own, the one the request has) is judged again by the planner and must not overlap a job the supplier already accepted. 409
    /// <see cref="ServiceRequestErrorCodes.SlotUnavailable"/> / <see cref="ServiceRequestErrorCodes.CommitmentConflict"/> otherwise, and nothing
    /// is changed. Used by the take, and by the acceptance of an offer that asks for a card first (SP-10b-b), which judges the time before it asks.
    /// </summary>
    private async Task EnsureQuoteTimeStillFreeAsync(ServiceRequest request, ServiceRequestQuote quote, CancellationToken cancellationToken)
    {
        if (quote is { ScheduledStartUtc: not null, ScheduledEndUtc: not null })
        {
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
    }

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
        if (request.Kind == ServiceRequestKind.Custom)
        {
            request.ResponseDueAt = null;
        }
        else if (request.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            var settings = await agenda.GetBookingSettingsAsync(request.SupplierOrgId, cancellationToken);
            request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
        }
        else if (request.GroupId is null && request.ResponseDueAt is { } due && due <= now)
        {
            request.ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes);
        }
    }

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

    private static void AppendQuoteEvent(ServiceRequest request, string kind, DateTime at, int? totalCents) =>
        request.QuoteEventsJson = ServiceRequestJson.Serialize(
            ServiceRequestQuoteRules.Append(
                ServiceRequestJson.ReadQuoteEvents(request.QuoteEventsJson),
                new ServiceRequestQuoteEvent(kind, at, totalCents)));
}
