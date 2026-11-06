using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A booking was made, confirmed, changed, cancelled or its stay ended: syncs the CRM of its booker right away, so the profile and its contacts
/// do not wait for the next run of the job (E07/F01/US02/T03, NF-88, NF-20). It only shortens the delay: the recurring job
/// <c>crm-profile-sync</c> guarantees the result, whether or not the event was delivered or this handler worked.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events</b>: <c>BookingCreated</c>, <c>BookingConfirmed</c>, <c>BookingModified</c>, <c>BookingCancelled</c> and
/// <c>CheckOutCompleted</c>. The event carries the booking by id (<see cref="DomainEvent.EntityId"/>) and the org it works for
/// (<see cref="DomainEvent.OrgId"/>, ADR-022-R6): the payload is not read, the booker is.</item>
/// <item><b>A failure never fails the event</b>: it is logged with the ids and the kind of error (never a message that could hold a value) and
/// swallowed, so the bus does not deliver the event again and the other handlers are not blamed. The job retries the booking.</item>
/// <item><b>Idempotent</b> (ADR-022-R7): the sync changes nothing the second time, so an event delivered again does no harm.</item>
/// <item>Without the key of the CRM index (<c>Crm:ContactIndexKey</c>) it does nothing and says nothing: the module is not set up.</item>
/// </list>
/// </remarks>
public sealed class CrmBookingEventHandler(ICrmProfileSyncService sync, ILogger<CrmBookingEventHandler> logger) : IEventHandler<DomainEvent>
{
    private static readonly DomainEventType[] HandledTypes =
    [
        DomainEventType.BookingCreated,
        DomainEventType.BookingConfirmed,
        DomainEventType.BookingModified,
        DomainEventType.BookingCancelled,
        DomainEventType.CheckOutCompleted,
    ];

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!HandledTypes.Contains(domainEvent.Type) || domainEvent.EntityId == Guid.Empty || !sync.IsAvailable)
            return;

        try
        {
            await sync.SyncBookingAsync(domainEvent.OrgId, domainEvent.EntityId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "CRM sync of booking {BookingId} of org {OrgId} failed on event {EventId} ({EventType}): {Error}; the job retries it",
                domainEvent.EntityId,
                domainEvent.OrgId,
                domainEvent.Id,
                domainEvent.Type,
                CrmDatabaseErrors.Describe(ex));
        }
    }
}
