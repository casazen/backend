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
public partial class ServiceRequestService : IServiceRequestOfferActions
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
