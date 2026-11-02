using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The conversation of a request on measure (SP-10b): the supplier reads the thread and writes in it, and so does the customer through the
// customer's area (IShowcaseRequestCustomerActions, called only by ShowcaseBookingManager, which has proved who is asking). The request is
// loaded, judged and saved by the code of the other actions (the active-supplier and Terms checks, the xmin check, the notifier); the messages
// are rows of ServiceRequestMessages, keyed by the supplier org and never read without it (ShowcaseInquiryTenancyTests). Nothing a message says
// goes in a log.
public partial class ServiceRequestService : IServiceRequestThreadActions
{
    public Task<ServiceRequestThread> GetThreadAsSupplierAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default) =>
        ReadThreadAsync(id, supplierOrgId, ServiceRequestMessageAuthor.Supplier, () => RequestNotFound(id), cancellationToken);

    public Task<ServiceRequestThread> ReadThreadAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default) =>
        ReadThreadAsync(
            id, supplierOrgId, ServiceRequestMessageAuthor.Customer, ShowcaseBookingManagementErrors.BookingNotFound, cancellationToken);

    public async Task<ServiceRequestMessageView> PostMessageAsSupplierAsync(
        Guid id,
        Guid supplierOrgId,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var text = ShowcaseInquiryRules.Message(body, [], minLength: 1) ?? throw ServiceRequestThreadErrors.InvalidMessage();

        // The same 404 for a request of another supplier, a host's request, a booking and one that does not exist.
        var isCustomOfSupplier = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == id
                     && r.SupplierOrgId == supplierOrgId
                     && r.RentalContext == ServiceRequestRentalContext.Showcase
                     && r.Kind == ServiceRequestKind.Custom,
                cancellationToken);
        if (!isCustomOfSupplier)
            throw RequestNotFound(id);

        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        var (message, notify) = await AddThreadMessageAsync(request, ServiceRequestMessageAuthor.Supplier, text, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: the supplier wrote in the thread", request.Id);

        // Committed: the customer is told even if the supplier has gone. A few lines in a row are one e-mail.
        if (notify)
            await notifier.NotifyShowcaseThreadMessageAsync(request, ServiceRequestMessageAuthor.Supplier, CancellationToken.None);

        return ToMessageView(message);
    }

    public async Task<ServiceRequestMessageView> PostMessageAsCustomerAsync(
        Guid id,
        Guid supplierOrgId,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var text = ShowcaseInquiryRules.Message(body, [], minLength: 1) ?? throw ServiceRequestThreadErrors.InvalidMessage();

        // The caller has proved who is asking; a request that is not a request on measure of that supplier is the 404 of the customer's area.
        var request = await repository.GetByIdAsync(id, cancellationToken);
        if (request is null
            || request.RentalContext != ServiceRequestRentalContext.Showcase
            || request.Kind != ServiceRequestKind.Custom
            || request.SupplierOrgId != supplierOrgId)
        {
            throw ShowcaseBookingManagementErrors.BookingNotFound();
        }

        if (!ServiceRequestThreadRules.IsOpen(request.Status))
            throw ServiceRequestThreadErrors.ThreadClosed();

        // A supplier that is not active answers nobody: the customer is told so, as for every other action on its request.
        if (await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) != SupplierStatus.Active)
            throw ShowcaseBookingErrors.InactiveSupplier();

        var (message, notify) = await AddThreadMessageAsync(request, ServiceRequestMessageAuthor.Customer, text, cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: its customer wrote in the thread", request.Id);

        // Committed: the supplier is told even if the customer has gone. A few lines in a row are one e-mail.
        if (notify)
            await notifier.NotifyShowcaseThreadMessageAsync(request, ServiceRequestMessageAuthor.Customer, CancellationToken.None);

        return ToMessageView(message);
    }

    /// <summary>
    /// The thread of a request on measure as <paramref name="reader"/> reads it. The messages the other parties wrote and the reader had not
    /// read are marked read; the answer shows each message as it was before this reading, so the screen can tell the new ones, and the next
    /// reading shows them read. <paramref name="notFound"/> makes the exception for a request that is not a request on measure of this supplier.
    /// </summary>
    private async Task<ServiceRequestThread> ReadThreadAsync(
        Guid id,
        Guid supplierOrgId,
        ServiceRequestMessageAuthor reader,
        Func<Exception> notFound,
        CancellationToken cancellationToken)
    {
        // The columns of the offer and the trace of the quotes are read for the supplier only: the customer reads its offer in its own area, with
        // the statement that keeps these columns out of it (ShowcaseBookingManager.RowsOf).
        var forSupplier = reader == ServiceRequestMessageAuthor.Supplier;
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == id
                        && r.SupplierOrgId == supplierOrgId
                        && r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Kind == ServiceRequestKind.Custom)
            .Select(r => new
            {
                r.Status,
                r.ResponseDueAt,
                r.Kind,
                r.QuoteSentAt,
                r.QuoteLinesJson,
                r.QuoteTotalCents,
                r.QuoteNote,
                r.QuoteScheduledStartUtc,
                r.QuoteScheduledEndUtc,
                r.QuoteEventsJson,
                r.CancellationReason,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw notFound();

        // Tracked: what the others wrote and the reader had not read is marked read below, on these rows.
        var messages = await db.ServiceRequestMessages
            .Where(m => m.OrgId == supplierOrgId && m.ServiceRequestId == id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

        var views = messages.Select(ToMessageView).ToList();
        var unread = messages.Where(m => m.Author != reader && m.ReadAt is null).ToList();
        if (unread.Count > 0)
        {
            var now = NowToTheMicrosecond();
            foreach (var message in unread)
                message.ReadAt = now;

            await db.SaveChangesAsync(cancellationToken);
        }

        var supplierActive = await ReadSupplierStatusAsync(supplierOrgId, cancellationToken) == SupplierStatus.Active;
        var canWrite = ServiceRequestThreadRules.IsOpen(request.Status) && supplierActive;

        // What only the supplier's reading adds (SP-10b-b): the offer that waits, where the negotiation is, and whether it may send an offer.
        ServiceRequestOffer? offer = null;
        string? negotiation = null;
        if (forSupplier)
        {
            offer = ServiceRequestOfferRules.PendingOf(
                request.Kind,
                request.Status,
                request.QuoteSentAt,
                request.QuoteLinesJson,
                request.QuoteTotalCents,
                request.QuoteNote,
                request.QuoteScheduledStartUtc,
                request.QuoteScheduledEndUtc,
                request.ResponseDueAt);
            negotiation = ServiceRequestNegotiation.Of(
                request.Status,
                messages.Any(m => m.Author == ServiceRequestMessageAuthor.Supplier) || ServiceRequestOfferRules.HasHistory(request.QuoteEventsJson),
                offerPending: offer is not null,
                lastOfferDeclined: ServiceRequestOfferRules.LastOfferDeclined(request.QuoteEventsJson),
                cancellationReason: request.CancellationReason);
        }

        return new ServiceRequestThread(
            id,
            request.Status,
            canWrite,
            request.Status == ServiceRequestStatus.Richiesto && offer is null ? request.ResponseDueAt : null,
            views,
            offer,
            negotiation,
            forSupplier && ServiceRequestOfferRules.CanSend(request.Kind, request.Status, supplierActive));
    }

    /// <summary>
    /// Adds a message to the thread of <paramref name="request"/> and saves it with the request, so the row version (<c>xmin</c>) is checked: a
    /// thread never grows under a request that was refused or cancelled a moment ago (409 <c>service_request_state_changed</c>). The first
    /// message of the supplier on a new request stops the clock of its answer. Returns the message, and whether the other party should be
    /// told (it was not told of an earlier message of the same author in the last few minutes).
    /// </summary>
    private async Task<(ServiceRequestMessage Message, bool Notify)> AddThreadMessageAsync(
        ServiceRequest request,
        ServiceRequestMessageAuthor author,
        string text,
        CancellationToken cancellationToken)
    {
        if (!ServiceRequestThreadRules.IsOpen(request.Status))
            throw ServiceRequestThreadErrors.ThreadClosed();

        var thread = db.ServiceRequestMessages.Where(m => m.OrgId == request.SupplierOrgId && m.ServiceRequestId == request.Id);
        if (await thread.CountAsync(cancellationToken) >= ShowcaseInquiryLimits.MaxMessagesPerRequest)
            throw ServiceRequestThreadErrors.ThreadFull();

        var now = NowToTheMicrosecond();
        var previousByAuthor = await thread.Where(m => m.Author == author).MaxAsync(m => (DateTime?)m.CreatedAt, cancellationToken);

        var message = new ServiceRequestMessage
        {
            ServiceRequestId = request.Id,
            OrgId = request.SupplierOrgId,
            Author = author,
            Body = text,
            CreatedAt = now,
        };
        db.ServiceRequestMessages.Add(message);

        request.UpdatedAt = now;

        // The first reply of the supplier stops the hour it has to answer. While an offer waits the clock is the customer's (the time it has to
        // answer the offer, SP-10b-b): a line the supplier writes beside its offer must not stop it, or the offer would never lapse.
        if (author == ServiceRequestMessageAuthor.Supplier && request.Status == ServiceRequestStatus.Richiesto && request.QuoteSentAt is null)
            request.ResponseDueAt = null;

        await SaveAsync(request, "thread message", cancellationToken);
        return (message, ServiceRequestThreadRules.ShouldNotify(previousByAuthor, now));
    }

    private static ServiceRequestMessageView ToMessageView(ServiceRequestMessage message) =>
        new(message.Id, message.Author, message.Body, message.CreatedAt, message.ReadAt);

    /// <summary>
    /// A request on measure is taken by accepting an offer, never by taking it or proposing a time on it: 422
    /// <see cref="ServiceRequestThreadErrors.OfferNeeded"/> (SP-10b). The work, its price and its time are agreed inside CasaZen.
    /// </summary>
    private static void EnsureNotOnMeasure(ServiceRequest request)
    {
        if (request.Kind == ServiceRequestKind.Custom)
            throw ServiceRequestThreadErrors.NeedsAnOffer();
    }
}
