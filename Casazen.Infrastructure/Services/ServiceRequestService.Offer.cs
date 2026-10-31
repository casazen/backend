using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The offer of the supplier on a request on measure (SP-10b-b). It is the quote of SP-04b (decision SP-D30: one engine, never a second
// mechanism): the same columns, the same one-offer-at-a-time rule, the same version (QuoteSentAt), the same deadline of the customer
// (Suppliers:Showcase:ProposalResponseMinutes), the same trace of events and the same calendar lock. What a request on measure adds is in the
// code that publishes it (a time is always brought, and the lines say how many of what), in the answers of the customer
// (ServiceRequestService.Customer.cs: AcceptOfferAsCustomerAsync, DeclineOfferAsCustomerAsync) and in the rule that no clock runs on the
// supplier once it has answered (RestartSupplierDeadlineAsync). The words of the conversation are never in a log: ids only.
public partial class ServiceRequestService : IServiceRequestOfferActions, IServiceRequestOfferCompletion
{
    public async Task<ServiceRequest> SendOfferAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        SendServiceRequestOfferCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The same 404 for a request of another supplier, a host's request, a booking and one that does not exist.
        await RequireRequestOnMeasureOfSupplierAsync(id, supplierOrgId, cancellationToken);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

        // The offer answers a request nobody took, refused or cancelled; the conversation is not closed.
        if (!ServiceRequestOfferRules.CanSend(request.Kind, request.Status, supplierActive: true))
            throw ServiceRequestOfferErrors.CannotSendOffer();

        // The lines, the total and the note are judged before anything is read or locked; a time that is missing is the plain 422 of a time.
        var (lines, total) = ServiceRequestOfferRules.Normalize(command.Lines);
        var note = ServiceRequestQuoteRules.NormalizeNote(command.Note);
        if (command.StartUtc == default)
            throw TimeInvalid();

        // The time is judged by the planner under the supplier's calendar lock, exactly as the time of a take or a proposal (409
        // supplier_slot_unavailable / supplier_commitment_conflict); the offer holds no slot, so it is judged again when the customer accepts.
        return await PublishQuoteAsync(
            request, supplierOrgId, userId, lines, total, note, command.StartUtc, command.EndUtc, cancellationToken);
    }

    public async Task<ServiceRequest> WithdrawOfferAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        await RequireRequestOnMeasureOfSupplierAsync(id, supplierOrgId, cancellationToken);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        if (ServiceRequestOfferRules.PendingOf(request) is null)
            throw ServiceRequestOfferErrors.NothingToWithdraw();

        // Taking an offer back is taking a quote back: the request is new again and, being a conversation, no clock runs on the supplier.
        return await WithdrawQuoteAsync(id, supplierOrgId, cancellationToken);
    }

    /// <summary>
    /// The card of the offer a customer accepted is blocked (the webhook says so): the request is taken now, if it still can be. Everything is read
    /// again under the supplier's calendar lock, then judged the way the customer's own acceptance judges it, and the card is charged after the time
    /// is checked and before the request changes state. What cannot be done is never an error for the webhook (the event is committed): the block
    /// is released and, when the customer is waiting for an answer, it is told by e-mail that nothing was charged.
    /// </summary>
    public async Task CompleteOfferAcceptanceAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (funds is null)
            return;

        try
        {
            // The supplier org keys the lock; the request has two parties and is read by its id (the allow-list of its table) with the context
            // and the kind as predicates, so only a request on measure from a showcase is ever completed this way.
            var supplierOrgId = await db.ServiceRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == requestId
                            && r.RentalContext == ServiceRequestRentalContext.Showcase
                            && r.Kind == ServiceRequestKind.Custom)
                .Select(r => (Guid?)r.SupplierOrgId)
                .FirstOrDefaultAsync(cancellationToken);
            if (supplierOrgId is not { } orgId)
                return;

            ServiceRequest? request = null;
            ServiceRequestQuote? offer = null;
            var taken = false;
            var staleBlock = false;
            string? notCompleted = null;
            try
            {
                await using var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken);
                request = await GetRequestOnMeasureOrThrow(requestId, orgId, cancellationToken);
                var now = NowToTheMicrosecond();

                // Already taken, or closed (the closing released the card): nothing to complete. Idempotent.
                if (request.Status != ServiceRequestStatus.Richiesto)
                    return;

                var held = await funds.FindHeldPaymentAsync(request.Id, cancellationToken);
                if (held is not { PayerKind: ServicePayerKind.Private, Status: ServicePaymentStatus.Authorized or ServicePaymentStatus.Held })
                    return;

                var standing = ServiceRequestQuoteRules.PendingOf(request);
                var supplierActive = await ReadSupplierStatusAsync(orgId, cancellationToken) == SupplierStatus.Active;
                if (standing is null
                    || standing.AnswerBy is not { } answerBy
                    || answerBy <= now
                    || !supplierActive
                    || !ServiceRequestOfferRules.IsPaymentOfOffer(held.CreatedAt, held.AmountCents, standing.SentAt, standing.TotalCents))
                {
                    // The offer is gone, replaced or lapsed, or the supplier is not active: a card blocked for it is released, never charged.
                    staleBlock = true;
                }
                else
                {
                    offer = standing;
                    await TakeOnQuoteAsync(
                        request,
                        standing,
                        ServiceRequestPaymentMode.Online,
                        now,
                        cancellationToken,
                        beforeTake: () => funds.CaptureForTakeAsync(request, cancellationToken));
                    await CommitAsync(transaction, cancellationToken);
                    taken = true;
                }
            }
            catch (DomainConflictException ex) when (ex.Code is ServiceRequestErrorCodes.SlotUnavailable or ServiceRequestErrorCodes.CommitmentConflict)
            {
                // The time of the offer is gone: the card is released and the customer is told that nothing was charged. The offer stays.
                staleBlock = true;
                notCompleted = NotCompletedBecauseOfTheTime;
            }
            catch (DomainConflictException ex) when (ex.Code is ServicePaymentErrors.AuthorizationExpired or ServicePaymentErrors.CaptureFailed)
            {
                // The card could not be charged (the block lapsed, the bank refused, Stripe did not answer): whatever is left of the block is
                // released, so what the customer is told is true — nothing was charged, the block is gone — and it starts again from a clean card.
                staleBlock = true;
                notCompleted = NotCompletedBecauseOfThePayment;
            }

            if (staleBlock)
                await ReleaseOfferPaymentAsync(requestId, CancellationToken.None);

            if (taken && request is not null && offer is not null)
            {
                logger.LogInformation("ServiceRequest {Id}: the card of the accepted offer is blocked, the request is taken", request.Id);
                await notifier.NotifyShowcaseQuoteAnsweredAsync(request, accepted: true, offer, CancellationToken.None);
            }
            else if (notCompleted is not null && request is not null)
            {
                logger.LogInformation("ServiceRequest {Id}: the accepted offer could not be completed ({Reason})", request.Id, notCompleted);
                await notifier.NotifyShowcaseOfferNotCompletedAsync(request, timeGone: notCompleted == NotCompletedBecauseOfTheTime, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never an error for the webhook: the event is committed. The customer's next acceptance finds the card and takes the request, and the
            // request closes (and the card with it) when the offer lapses.
            db.ChangeTracker.Clear();
            logger.LogError(ex, "ServiceRequest {Id}: the acceptance of the offer could not be completed after the card was blocked", requestId);
        }
    }

    private const string NotCompletedBecauseOfTheTime = "time_gone";
    private const string NotCompletedBecauseOfThePayment = "payment";

    /// <summary>
    /// 404 (the plain one of the service requests) unless <paramref name="id"/> is a request on measure of the supplier org: a request of another
    /// supplier, a host's request, a booking from a slot and a request that does not exist all answer the same.
    /// </summary>
    private async Task RequireRequestOnMeasureOfSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var isOnMeasure = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == id
                     && r.SupplierOrgId == supplierOrgId
                     && r.RentalContext == ServiceRequestRentalContext.Showcase
                     && r.Kind == ServiceRequestKind.Custom,
                cancellationToken);
        if (!isOnMeasure)
            throw RequestNotFound(id);
    }
}
