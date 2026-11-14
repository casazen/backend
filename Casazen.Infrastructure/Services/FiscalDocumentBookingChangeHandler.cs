using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The correction of the documents of a stay when its booking changes (E12/F01/US01/T03, NF-83, NF-112, ADR-022-R7, I12): the consumer of the events the booking lifecycle records
/// (<see cref="DomainEventType.BookingModified"/>: dates, guests, amounts; <see cref="DomainEventType.BookingCancelled"/>; <see cref="DomainEventType.BookingNoShowMarked"/>) and of the
/// refund of a payment of the booking (<see cref="DomainEventType.PaymentRefunded"/>). It asks <see cref="IFiscalDocumentService.ApplyBookingChangeAsync"/>, which prepares the
/// credit note (the reversal receipt of a receipt) and the new draft; the org is the event's (ADR-022-R6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Idempotent per event.</b> The service decides from the state of the booking and of its documents (what is credited already, what the guest is charged), never from the
/// event: the same event delivered again, two events of one change and the events of a change delivered out of order (the booking is read as it is now) give one correction.</item>
/// <item><b>An answer is not a failure.</b> A refusal of the rules (the profile is incomplete, no tax rule, the data of the guest is missing) is logged with its code and ids and not
/// delivered again: the host is told by the «To do» the service raised, and the daily run makes the draft the correction could not.</item>
/// <item><b>The state that is not there yet is waited for.</b> A cancelled booking whose refund is still under way cannot be credited by what it will refund: the event is delivered again
/// (<see cref="FiscalDocumentChangePendingException"/>), and the refund that succeeds records <c>PaymentRefunded</c>, which prepares the credit note. Any other failure (the database) is
/// logged with the type of the error and thrown again, so the outbox delivers the event again.</item>
/// <item>Nothing while <c>Features:FiscalDocumentIssuance</c> is off. No log carries a value of the guest or of a document.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentBookingChangeHandler(
    IFiscalDocumentService documents,
    IFeatureFlags featureFlags,
    ILogger<FiscalDocumentBookingChangeHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (ChangeOf(domainEvent.Type) is not { } change || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return;

        if (BookingOf(domainEvent) is not { } bookingId)
        {
            logger.LogWarning("Event {EventId} ({EventType}) of org {OrgId} names no booking: no document to correct", domainEvent.Id, domainEvent.Type, domainEvent.OrgId);
            return;
        }

        try
        {
            var outcome = await documents.ApplyBookingChangeAsync(bookingId, change, ct);
            logger.LogInformation(
                "Event {EventId} ({EventType}): booking {BookingId} of org {OrgId}: {Refreshed} drafts made again, {Discarded} discarded, {Credits} credit notes prepared, {Issued} documents issued, {Todos} to do",
                domainEvent.Id,
                domainEvent.Type,
                bookingId,
                domainEvent.OrgId,
                outcome.DraftsRefreshed,
                outcome.DraftsDiscarded,
                outcome.CreditNotesPrepared,
                outcome.DocumentsIssued,
                outcome.TodosRaised);
        }
        catch (FiscalDocumentChangePendingException)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}): the refund of booking {BookingId} of org {OrgId} is not done yet: the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                bookingId,
                domainEvent.OrgId);
            throw;
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Event {EventId}: booking {BookingId} of org {OrgId} not found, no document to correct", domainEvent.Id, bookingId, domainEvent.OrgId);
        }
        catch (DomainException ex)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}): no correction made now for booking {BookingId} of org {OrgId} ({Code})",
                domainEvent.Id,
                domainEvent.Type,
                bookingId,
                domainEvent.OrgId,
                ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Event {EventId} ({EventType}): the documents of booking {BookingId} of org {OrgId} could not be corrected ({Error}): the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                bookingId,
                domainEvent.OrgId,
                ex.GetType().Name);
            throw;
        }
    }

    /// <summary>What a booking event asks of the documents; <c>null</c> for an event that is not one of the changes.</summary>
    internal static BookingChange? ChangeOf(DomainEventType type) => type switch
    {
        DomainEventType.BookingModified => BookingChange.Modified,
        DomainEventType.BookingCancelled => BookingChange.Cancelled,
        DomainEventType.BookingNoShowMarked => BookingChange.NoShow,
        DomainEventType.PaymentRefunded => BookingChange.Refunded,
        _ => null,
    };

    /// <summary>The booking of an event: its entity for a booking event, the booking named by the payload for the refund of a payment; <c>null</c> when the payload says none.</summary>
    private static Guid? BookingOf(DomainEvent domainEvent)
    {
        if (domainEvent.Type != DomainEventType.PaymentRefunded)
            return domainEvent.EntityId;

        try
        {
            var payload = JsonSerializer.Deserialize<PaymentRefundedPayload>(domainEvent.PayloadJson, DomainEventRecorder.PayloadJsonOptions);
            return payload is { BookingId: var id } && id != Guid.Empty ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The refund of a cancelled booking (or of a no-show) is still under way (E12/F01/US01/T03): the credit note of its document follows what is refunded, so the event is delivered again
/// instead of being consumed with no effect; the refund that succeeds records <c>PaymentRefunded</c>, which prepares it.
/// </summary>
public sealed class FiscalDocumentChangePendingException(Guid bookingId)
    : Exception($"A refund of booking {bookingId} is not done yet: the correction of its document is made when the event is delivered again.");
