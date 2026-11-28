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
        var text = ShowcaseBookingManagementRules.NormalizeReason(reason);

        ServiceRequest request;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);

            // A second call, or a retry after a lost answer: the customer has already cancelled it. Nothing happens again.
            if (request is { Status: ServiceRequestStatus.Annullato, CancelledBy: ServiceRequestActorParty.Customer })
                return request;

            var now = NowToTheMicrosecond();

            if (!ShowcaseBookingManagementRules.CanCancel(request.Status, request.ScheduledStartUtc, now)
                || !ServiceRequestStateMachine.CanCancel(request.Status, ServiceRequestActorParty.Customer))
            {
                throw ShowcaseBookingManagementErrors.CancelRefused();
            }

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

        // Committed: the notifications do not go with the request. A customer who closes the tab after the cancellation is saved is
        // still heard by the supplier, and a retry (which finds it cancelled and does nothing) would never tell it.
        await notifier.NotifyShowcaseCancelledByCustomerAsync(request, CancellationToken.None);

        // SP-15c-a: the block on the customer's card is released, or the money charged is given back in full (cancelling is always free,
        // decision D6, the commission included). The cancellation stands whatever Stripe does now: the funds job settles the rest.
        await SettleClosedMoneyAsync(request, CancellationToken.None);
        return request;
    }

    public async Task<ServiceRequest> RescheduleAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime startUtc,
        CancellationToken cancellationToken = default)
    {
        var start = UtcDateTime.Normalize(startUtc);

        if (start.Ticks % TimeSpan.TicksPerMinute != 0)
            throw ShowcaseBookingErrors.InvalidFields([ShowcaseBookingFields.StartUtc]);

        ServiceRequest request;
        DateTime previousStart;
        bool proposalDropped;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
            var now = NowToTheMicrosecond();

            if (request.Status != ServiceRequestStatus.Richiesto
                || request.QuoteSentAt is not null
                || request.ScheduledStartUtc is not { } currentStart
                || request.ScheduledEndUtc is not { } currentEnd)
            {
                throw ShowcaseBookingManagementErrors.RescheduleRefused();
            }

            var supplierActive = await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) == SupplierStatus.Active;
            if (!supplierActive)
                throw ShowcaseBookingErrors.InactiveSupplier();

            var service = request.ServiceListingId is { } listingId
                ? await catalog.FindForRequestAsync(supplierOrgId, listingId, cancellationToken)
                : null;
            if (service is null
                || !ShowcaseBookingManagementRules.CanReschedule(request.Status, hasTime: true, supplierActive, service.IsRequestable))
            {
                throw ShowcaseBookingManagementErrors.RescheduleRefused();
            }

            // The time it already has: a retry after a lost answer, or a double click. Nothing is moved, nobody is told again.
            if (start == currentStart)
                return request;

            if (!ShowcaseBookingRules.IsWithinSlotWindow(start, now))
                throw SlotUnavailable();

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

            request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
            ClearProposal(request);
            request.UpdatedAt = now;

            await SaveAsync(request, "reschedule by the customer", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: its customer moved it to another time", request.Id);

        // Committed: the supplier is told even if the customer has gone.
        await notifier.NotifyShowcaseRescheduledAsync(request, previousStart, proposalDropped, CancellationToken.None);
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

            var (proposedStart, proposedEnd, madeAt) = await RequireProposalToAnswerAsync(request, supplierOrgId, now, cancellationToken);

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
            await EnsureNoOverlappingCommitmentAsync(
                supplierOrgId, request.Id, proposedStart, proposedEnd, cancellationToken);

            await CaptureHeldMoneyAsync(request, cancellationToken);

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

        // Committed: both parties are told even if the customer has gone.
        await notifier.NotifyShowcaseProposalAnsweredAsync(request, accepted: true, proposedAt, CancellationToken.None);
        return request;
    }

    public async Task<ServiceRequest> RejectProposalAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
        var now = NowToTheMicrosecond();
        var (_, _, proposedAt) = await RequireProposalToAnswerAsync(request, supplierOrgId, now, cancellationToken);

        var settings = await agenda.GetBookingSettingsAsync(supplierOrgId, cancellationToken);
        ClearProposal(request);
        request.ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
        request.UpdatedAt = now;
        await SaveAsync(request, "proposal rejected by the customer", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: its customer turned the proposed time down", request.Id);

        // Committed: the supplier is told even if the customer has gone.
        await notifier.NotifyShowcaseProposalAnsweredAsync(request, accepted: false, proposedAt, CancellationToken.None);
        return request;
    }

    public async Task<ServiceRequest> AcceptQuoteAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default)
    {
        ServiceRequest request;
        ServiceRequestQuote quote;
        await using (var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken))
        {
            request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
            var now = NowToTheMicrosecond();

            if (request.Kind == ServiceRequestKind.Custom)
                throw ShowcaseBookingQuoteErrors.QuoteMissing();

            quote = await RequireQuoteToAnswerAsync(request, supplierOrgId, quoteSentAt, now, cancellationToken);

            // Checked again under the lock, like the host's acceptance: the time may have been taken, or closed, since the supplier quoted.
            // Then 409 and the quote stays: the customer can still turn it down, or cancel.
            await TakeOnQuoteAsync(request, quote, await ResolvePaymentModeAsync(request, cancellationToken), now, cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation("ServiceRequest {Id}: its customer accepted the quote", request.Id);

        // Committed: both parties are told even if the customer has gone.
        await notifier.NotifyShowcaseQuoteAnsweredAsync(request, accepted: true, quote, CancellationToken.None);
        return request;
    }

    public async Task<ServiceRequest> RejectQuoteAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default)
    {
        var request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
        var now = NowToTheMicrosecond();

        if (request.Kind == ServiceRequestKind.Custom)
            throw ShowcaseBookingQuoteErrors.QuoteMissing();

        var quote = await RequireQuoteToAnswerAsync(request, supplierOrgId, quoteSentAt, now, cancellationToken);

        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Rejected, now, quote.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "quote rejected by the customer", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: its customer turned the quote down", request.Id);

        // Committed: the supplier is told even if the customer has gone.
        await notifier.NotifyShowcaseQuoteAnsweredAsync(request, accepted: false, quote, CancellationToken.None);
        return request;
    }

    public async Task<OfferAcceptance> AcceptOfferAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime offerSentAt,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            ServiceRequest request;
            ServiceRequestQuote offer;
            var taken = false;
            var cardWasThere = false;
            try
            {
                await using var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);

                // Read after the lock: whoever held it before is seen, so an offer the supplier replaced a moment ago, a request the supplier or
                // another call closed, and a time another booking took are judged as they are now. Of two acceptances of the same offer the
                // second finds the request taken (no offer waits) or loses the row version: one transition, never two.
                request = await GetRequestOnMeasureOrThrow(id, supplierOrgId, cancellationToken);
                var now = NowToTheMicrosecond();

                offer = await RequireQuoteToAnswerAsync(request, supplierOrgId, offerSentAt, now, cancellationToken, asOffer: true);

                var held = funds is null ? null : await funds.FindHeldPaymentAsync(request.Id, cancellationToken);
                cardWasThere = held is { PayerKind: ServicePayerKind.Private, Status: ServicePaymentStatus.Authorized or ServicePaymentStatus.Held }
                               && ServiceRequestOfferRules.IsPaymentOfOffer(held.CreatedAt, held.AmountCents, offer.SentAt, offer.TotalCents);
                var paidInside = !cardWasThere
                                 && funds is not null
                                 && await funds.CanAuthorizeAsync(supplierOrgId, offer.TotalCents, cancellationToken);

                if (paidInside)
                {
                    // The card is to be asked for. The time is judged now, so that nobody is asked to pay for a time that is already gone; nothing is
                    // changed or held. The request is taken when the card is blocked.
                    await EnsureQuoteTimeStillFreeAsync(request, offer, cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                }
                else
                {
                    // The time of the offer is checked again under the lock, with the planner and the jobs the supplier already accepted; a card
                    // that is there is charged after the check and before the request changes state. A time that is gone, or a card that cannot be
                    // charged, is 409 and nothing is saved: the offer stays, and the customer can turn it down, write to the supplier or cancel.
                    // The price of the request is the total of the offer, whatever the supplier or anybody else does afterwards.
                    await TakeOnQuoteAsync(
                        request,
                        offer,
                        cardWasThere ? ServiceRequestPaymentMode.Online : await ResolvePaymentModeAsync(request, cancellationToken),
                        now,
                        cancellationToken,
                        beforeTake: cardWasThere ? () => funds!.CaptureForTakeAsync(request, cancellationToken) : null);
                    await CommitAsync(transaction, cancellationToken);
                    taken = true;
                }
            }
            catch (DomainConflictException ex) when (ex.Code is ServiceRequestErrorCodes.SlotUnavailable or ServiceRequestErrorCodes.CommitmentConflict)
            {
                await ReleaseOfferPaymentAsync(id, CancellationToken.None);
                throw;
            }

            if (taken)
            {
                logger.LogInformation("ServiceRequest {Id}: its customer accepted the offer", request.Id);

                // Committed: both parties are told even if the customer has gone.
                await notifier.NotifyShowcaseQuoteAnsweredAsync(request, accepted: true, offer, CancellationToken.None);
                return new OfferAcceptance(request, null);
            }

            var session = await funds!.OpenOfferPaymentAsync(request, ServiceRequestOfferRules.PendingOf(request)!, cancellationToken);
            if (session is not null)
            {
                logger.LogInformation("ServiceRequest {Id}: its customer accepted the offer, the card is to be blocked", request.Id);
                return new OfferAcceptance(request, session);
            }

            if (attempt >= 1)
                throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
    }

    public async Task<ServiceRequest> DeclineOfferAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        DateTime offerSentAt,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOnMeasureOrThrow(id, supplierOrgId, cancellationToken);
        var now = NowToTheMicrosecond();
        var offer = await RequireQuoteToAnswerAsync(request, supplierOrgId, offerSentAt, now, cancellationToken, asOffer: true);

        await RestartSupplierDeadlineAsync(request, now, cancellationToken);
        ClearQuote(request);
        AppendQuoteEvent(request, ServiceRequestQuoteEventKinds.Rejected, now, offer.TotalCents);
        request.UpdatedAt = now;
        await SaveAsync(request, "offer declined by the customer", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: its customer turned the offer down", request.Id);

        await ReleaseOfferPaymentAsync(request.Id, CancellationToken.None);

        // Committed: the supplier is told even if the customer has gone.
        await notifier.NotifyShowcaseQuoteAnsweredAsync(request, accepted: false, offer, CancellationToken.None);
        return request;
    }

    private async Task<ServiceRequest> GetRequestOnMeasureOrThrow(Guid id, Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var request = await GetShowcaseRequestOrThrow(id, supplierOrgId, cancellationToken);
        return request.Kind == ServiceRequestKind.Custom ? request : throw ShowcaseBookingManagementErrors.BookingNotFound();
    }

    /// <summary>
    /// The quote a new showcase request is waiting for its customer to answer, while there is still time to: 422
    /// <see cref="ShowcaseBookingQuoteErrors.NoQuote"/> when there is none (never sent, withdrawn, answered, or the request moved on),
    /// 409 <see cref="ServiceRequestErrorCodes.QuoteChanged"/> when the customer read another one (the supplier replaced it meanwhile), 422
    /// <see cref="ShowcaseBookingQuoteErrors.QuoteExpired"/> once its deadline (<c>ResponseDueAt</c>, which a quote of a showcase request
    /// moves to the customer's time) has passed: the upkeep job cancels the request on its next run; 422
    /// <c>supplier_booking_supplier_unavailable</c> when the supplier is not active. In the end it is the rule the lookup shows
    /// <c>canRespondToQuote</c> from that decides (<see cref="ShowcaseBookingManagementRules.CanRespondToQuote"/>), so the page and the API
    /// cannot disagree.
    /// </summary>
    private async Task<ServiceRequestQuote> RequireQuoteToAnswerAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        DateTime quoteSentAt,
        DateTime now,
        CancellationToken cancellationToken,
        bool asOffer = false)
    {
        var quote = ServiceRequestQuoteRules.PendingOf(request)
            ?? throw (asOffer ? ServiceRequestOfferErrors.OfferMissing() : ShowcaseBookingQuoteErrors.QuoteMissing());
        if (!ServiceRequestQuoteRules.IsSameVersion(quote.SentAt, quoteSentAt))
        {
            throw asOffer
                ? ServiceRequestOfferErrors.ChangedOffer()
                : new DomainConflictException(ServiceRequestErrorCodes.QuoteChanged, ServiceRequestErrorCodes.QuoteChangedMessageKey);
        }

        if (quote.AnswerBy is not { } answerBy || answerBy <= now)
            throw asOffer ? ServiceRequestOfferErrors.OfferLapsed() : ShowcaseBookingQuoteErrors.QuoteLapsed();

        var supplierActive = await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) == SupplierStatus.Active;
        if (!supplierActive)
            throw ShowcaseBookingErrors.InactiveSupplier();

        if (!ShowcaseBookingManagementRules.CanRespondToQuote(request.Status, quotePending: true, answerBy, now, supplierActive))
            throw asOffer ? ServiceRequestOfferErrors.OfferMissing() : ShowcaseBookingQuoteErrors.QuoteMissing();

        return quote;
    }

    /// <summary>
    /// The proposal a new showcase request is waiting for its customer to answer, while there is still time to: 422
    /// <see cref="ShowcaseBookingManagementErrors.NoProposal"/> when there is none (never made, answered, or the request moved on),
    /// 422 <see cref="ShowcaseBookingManagementErrors.ProposalExpired"/> once its deadline (<c>ResponseDueAt</c>, which a proposal of a
    /// showcase request moves to the customer's time) has passed: the upkeep job cancels the request on its next run; 422
    /// <c>supplier_booking_supplier_unavailable</c> when the supplier is not active. In the end it is the rule the lookup shows
    /// <c>canRespondToProposal</c> from that decides (<see cref="ShowcaseBookingManagementRules.CanRespondToProposal"/>), so the page and
    /// the API cannot disagree.
    /// </summary>
    private async Task<(DateTime Start, DateTime End, DateTime ProposedAt)> RequireProposalToAnswerAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var pending = request is { ProposedStartUtc: not null, ProposedEndUtc: not null, ProposedAt: not null };
        if (request.Status != ServiceRequestStatus.Richiesto || !pending)
            throw ShowcaseBookingManagementErrors.ProposalMissing();

        if (request.ResponseDueAt is not { } answerBy || answerBy <= now)
            throw ShowcaseBookingManagementErrors.ProposalLapsed();

        var supplierActive = await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) == SupplierStatus.Active;
        if (!supplierActive)
            throw ShowcaseBookingErrors.InactiveSupplier();

        if (!ShowcaseBookingManagementRules.CanRespondToProposal(request.Status, pending, answerBy, now, supplierActive))
            throw ShowcaseBookingManagementErrors.ProposalMissing();

        return (request.ProposedStartUtc!.Value, request.ProposedEndUtc!.Value, request.ProposedAt!.Value);
    }

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

    private static DomainConflictException SlotUnavailable() =>
        new(ServiceRequestErrorCodes.SlotUnavailable, ServiceRequestErrorCodes.SlotUnavailableMessageKey);

    private DateTime NowToTheMicrosecond() => UtcDateTime.TruncateToMicroseconds(Now());
}
