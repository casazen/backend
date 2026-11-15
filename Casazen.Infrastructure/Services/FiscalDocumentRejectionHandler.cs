using System.Text.Json;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The «Da fare» of an electronic invoice the SDI rejected (E17/F01/US02/T03, NF-86, ADR-026, ADR-022): the consumer of <see cref="DomainEventType.FiscalDocumentSdiRejected"/> raises the item
/// «Correggi la fattura» (type <c>FiscalDocument</c>, action <see cref="ActivityAction.CorrectInvoice"/>, reason <c>sdi_rejected</c>, urgency high, entity the document), and closes it when the
/// invoice is corrected (<see cref="DomainEventType.FiscalDocumentCorrected"/>) or credited (<see cref="DomainEventType.FiscalDocumentCreditNoteIssued"/>, the credited document of its payload).
/// The org is the event's (ADR-022-R6). The reason in plain words is read by the client from <c>GET api/fiscal/documents/{id}/sdi</c>: the item names the document and a code, never a word of it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>At least once.</b> The item is raised once while it is open (<see cref="IInternalActivityService.CreateAsync"/> is idempotent per type and entity) and closing an item already closed changes
/// nothing, so an event delivered again does no harm. A failure (the database) is logged with the type of the error and thrown again, so the outbox delivers the event again.</item>
/// <item><b>No item for what is not rejected any more.</b> The events are not delivered in order: a rejection whose document was corrected, or credited, before the event was delivered raises nothing
/// (its closing event found no item and went away, so an item raised now would never close).</item>
/// <item><b>No item for a self-billed invoice.</b> A TD17 the SDI rejected (<see cref="FiscalDocumentPurpose.SelfBilling"/>) raises nothing here: «Correggi la fattura» leads to the correction of an
/// invoice to a guest, which a TD17 has not (<see cref="SelfBillingErrors.NotCorrectable"/>); its rejection is read on the self-billed invoices (FIX/RES-DOCS).</item>
/// <item><b>The property of the item.</b> The property of the document; a document with none (an owner invoice), or whose property is gone, gets an item of the organization.</item>
/// <item><b>Live, not only listed.</b> The hosts of the booking of the document are told with a push once the item is raised (<see cref="FiscalDocumentLiveRefresh.TellOfRejection"/>): the bell
/// gets its entry and the open pages read the document and the «To do» again, with no reload. One push per document (stable delivery key), best effort after the item is saved.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentRejectionHandler(
    AppDbContext db,
    IInternalActivityService activities,
    IDocumentTexts texts,
    ILogger<FiscalDocumentRejectionHandler> logger,
    IPushNotificationService? push = null) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.EntityId == Guid.Empty || domainEvent.Type is not (DomainEventType.FiscalDocumentSdiRejected or DomainEventType.FiscalDocumentCorrected or DomainEventType.FiscalDocumentCreditNoteIssued))
            return;

        try
        {
            switch (domainEvent.Type)
            {
                case DomainEventType.FiscalDocumentSdiRejected:
                    await RaiseAsync(domainEvent, ct);
                    break;
                case DomainEventType.FiscalDocumentCorrected:
                    // The rejected document is discarded: every «To do» still open on it is done, the one of its correction and one to look at it.
                    await CloseAsync(domainEvent, domainEvent.EntityId, ct);
                    await CloseAsync(domainEvent, domainEvent.EntityId, ct, nameof(ActivityAction.ReviewFiscalDocument));
                    break;
                default:
                    if (CreditedDocumentOf(domainEvent) is { } credited)
                        await CloseAsync(domainEvent, credited, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Event {EventId} ({EventType}): the «To do» of the rejection of document {DocumentId} of org {OrgId} could not be updated ({Error}): the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.EntityId,
                domainEvent.OrgId,
                ex.GetType().Name);
            throw;
        }
    }

    private async Task RaiseAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        var documentId = domainEvent.EntityId;
        var document = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.OrgId, d.PropertyId, d.BookingId, d.Source, d.Status, d.SdiStatus, d.Purpose })
            .FirstOrDefaultAsync(ct);
        if (document is null || document.Source != FiscalDocumentSource.CasaZen)
        {
            logger.LogWarning("Event {EventId}: fiscal document {DocumentId} of org {OrgId} not found: no «To do» raised", domainEvent.Id, documentId, domainEvent.OrgId);
            return;
        }

        if (document.Status != FiscalDocumentStatus.Issued || document.SdiStatus != FiscalDocumentSdiStatuses.Rejected)
        {
            logger.LogInformation("Event {EventId}: fiscal document {DocumentId} is not rejected any more: no «To do» raised", domainEvent.Id, documentId);
            return;
        }

        // A self-billed invoice (TD17) has no correction (422 self_billing_not_correctable): «Correggi la fattura» would lead nowhere, and its page of the self-billed invoices shows the rejection.
        if (document.Purpose == FiscalDocumentPurpose.SelfBilling)
        {
            logger.LogInformation("Event {EventId}: fiscal document {DocumentId} is a self-billed invoice: no «To do» to correct it raised", domainEvent.Id, documentId);
            return;
        }

        // The events are not delivered in order: an owner invoice credited since (its credit note issued, the way an owner invoice is corrected) had its closing event delivered before this
        // one, so an item raised now would never close. A guest invoice is never credited once rejected: one that has a credit note issued before (while it was with the SDI) or only the
        // draft of one still gets its item, since its correction is what the host has to see to.
        if (document.Purpose == FiscalDocumentPurpose.OwnerInvoice
            && await db.FiscalDocuments.AsNoTracking().AnyAsync(d => d.CreditedDocumentId == documentId && d.Status == FiscalDocumentStatus.Issued, ct))
        {
            logger.LogInformation("Event {EventId}: fiscal document {DocumentId} was credited since: no «To do» raised", domainEvent.Id, documentId);
            return;
        }

        // A «To do» is one per document while it is open, whatever its action: an item to look at the document (its booking changed) would take the place of this one, so it is closed first.
        // The correction makes the replacement from the booking as it is now, so the change is not lost.
        if (await activities.FindOpenAsync(document.OrgId, nameof(ActivityAction.ReviewFiscalDocument), FiscalDocumentTodos.EntityType, documentId, ct) is { } review)
        {
            await activities.CloseAsync(review.Id, InternalActivity.ClosedBySystem, ct);
            logger.LogInformation("Event {EventId}: «To do» {ActivityId} to look at document {DocumentId} closed, the «To do» of its correction takes its place", domainEvent.Id, review.Id, documentId);
        }

        var request = new CreateTodoRequest
        {
            Type = ActivityType.FiscalDocument,
            Action = ActivityAction.CorrectInvoice,
            EntityType = FiscalDocumentTodos.EntityType,
            EntityId = documentId,
            Urgency = ActivityUrgency.High,
            PropertyId = document.PropertyId,
            OrgId = document.OrgId,
            Reason = FiscalDocumentSdiTodos.RejectedReason,
        };
        try
        {
            await activities.CreateAsync(request, ct);
        }
        catch (NotFoundException) when (document.PropertyId is not null)
        {
            // The property is gone (deleted since): the item is the organization's, so a rejection is never left without its «To do».
            await activities.CreateAsync(request with { PropertyId = null }, ct);
        }

        logger.LogInformation("Event {EventId}: «To do» «correct the invoice» raised for document {DocumentId} of org {OrgId}", domainEvent.Id, documentId, domainEvent.OrgId);

        // The live notice (bell, pop-up, open pages) only for a document of a booking. Best effort: the item is saved, so nothing here fails the event.
        if (push is not null && document.BookingId is not null)
            await TellTheHostsAsync(documentId, document.OrgId, document.BookingId, ct);
    }

    private async Task TellTheHostsAsync(Guid documentId, Guid orgId, Guid? bookingId, CancellationToken ct)
    {
        try
        {
            var orgLanguage = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.DefaultLanguage).FirstOrDefaultAsync(ct);
            FiscalDocumentLiveRefresh.TellOfRejection(push, texts, documentId, bookingId, orgLanguage, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Push about the rejected fiscal document {DocumentId} of org {OrgId} could not be prepared ({Error})", documentId, orgId, ex.GetType().Name);
        }
    }

    private async Task CloseAsync(DomainEvent domainEvent, Guid documentId, CancellationToken ct, string action = nameof(ActivityAction.CorrectInvoice))
    {
        var open = await activities.FindOpenAsync(domainEvent.OrgId, action, FiscalDocumentTodos.EntityType, documentId, ct);
        if (open is null)
            return;

        await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        logger.LogInformation("Event {EventId} ({EventType}): «To do» {Action} of document {DocumentId} of org {OrgId} closed", domainEvent.Id, domainEvent.Type, action, documentId, domainEvent.OrgId);
    }

    /// <summary>The document a credit note credits, from the payload of its event; <c>null</c> when the payload names none.</summary>
    private static Guid? CreditedDocumentOf(DomainEvent domainEvent)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<FiscalDocumentIssuedPayload>(domainEvent.PayloadJson, DomainEventRecorder.PayloadJsonOptions);
            return payload?.CreditedDocumentId is { } id && id != Guid.Empty ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
