using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The draft of the document of a stay at its check-out (E12/F01/US01/T02, NF-83, ADR-022-R7, ADR-026-R3): the consumer of <see cref="DomainEventType.CheckOutCompleted"/> makes the
/// draft through <see cref="IFiscalDocumentService.EnsureDraftAsync"/> when the booking has no live document. The org is the event's (ADR-022-R6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once.</b> A draft is made only for a booking with no live document, under the lock of the booking: the event delivered again, the daily run and the host's own draft
/// never make a second one.</item>
/// <item><b>Never the host's problem.</b> The check-out committed before the event was delivered; whatever goes wrong here never touches it. A refusal of the rules (the data of the
/// guest or of the issuer is missing, no tax rule, a booking that is not active, the issuance is off) is an answer, not a failure: it is logged with its code and ids and the daily run
/// makes the draft once the data is there; the event is not delivered again for it. Any other failure (the database) is logged with the type of the error and ids, and thrown again, so
/// the outbox delivers the event again, as it does for the other consumers.</item>
/// <item>Nothing while <c>Features:FiscalDocumentIssuance</c> is off. No log carries a value of the guest or of the document.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentCheckOutHandler(
    IFiscalDocumentService documents,
    IFeatureFlags featureFlags,
    ILogger<FiscalDocumentCheckOutHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.CheckOutCompleted || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return;

        var bookingId = domainEvent.EntityId;
        try
        {
            var outcome = await documents.EnsureDraftAsync(bookingId, userId: null, ct);
            logger.LogInformation(
                "Event {EventId}: fiscal document of booking {BookingId} of org {OrgId} at the check-out: {Outcome}",
                domainEvent.Id,
                bookingId,
                domainEvent.OrgId,
                outcome);
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Event {EventId}: booking {BookingId} of org {OrgId} not found, no document to draft", domainEvent.Id, bookingId, domainEvent.OrgId);
        }
        catch (DomainException ex)
        {
            logger.LogInformation(
                "Event {EventId}: no draft yet for booking {BookingId} of org {OrgId} ({Code}): the daily run makes it when the data is there",
                domainEvent.Id,
                bookingId,
                domainEvent.OrgId,
                ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Event {EventId}: the draft of booking {BookingId} of org {OrgId} could not be made ({Error}): the event is delivered again",
                domainEvent.Id,
                bookingId,
                domainEvent.OrgId,
                ex.GetType().Name);
            throw;
        }
    }
}
