using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The supplier completed the cleaning of a stay (<see cref="DomainEventType.ServiceRequestCompleted"/>, casazen/backend#978): when the booking flow of the org declares
/// the property ready «when the cleaning is done», the property of the stay is declared ready now, in the host's place
/// (<see cref="IStayLifecycleService.DeclarePropertyReadyAutomaticallyAsync"/>), which closes the cleaning «To do» of the stay and tells the hosts live.
/// </summary>
/// <remarks>
/// A request that is not a cleaning, or not tied to a stay, changes nothing; a stay not checked out yet is declared at its check-out (the check-out reads the cleaning
/// done). Idempotent: a redelivered event finds the property declared and does nothing. The org is the event's (ADR-022-R6).
/// </remarks>
public sealed class PropertyReadyAutomationHandler(
    AppDbContext db,
    IStayLifecycleService stayLifecycle) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Type != DomainEventType.ServiceRequestCompleted || domainEvent.EntityId == Guid.Empty)
            return;

        var request = await db.ServiceRequests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(r => r.Id == domainEvent.EntityId && r.OrgId == domainEvent.OrgId)
            .Select(r => new { r.BookingId, r.Category })
            .FirstOrDefaultAsync(ct);
        if (request?.BookingId is not { } bookingId || request.Category != ServiceCategories.Cleaning)
            return;

        try
        {
            await stayLifecycle.DeclarePropertyReadyAutomaticallyAsync(bookingId, ct);
        }
        catch (NotFoundException)
        {
            // The stay is gone (erased since): nothing to declare, and a retry would find the same.
        }
    }
}
