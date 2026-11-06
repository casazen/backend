using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The arrival and the departure of a stay as states of its check-in on Octorate (E03/F01/US01/T03, NF-45): on
/// <see cref="DomainEventType.CheckInCompleted"/> and <see cref="DomainEventType.CheckOutCompleted"/>, recorded by the stay lifecycle, it queues
/// <see cref="OctorateStayStateJob"/>, which sends the state once, keeps it on <see cref="OctorateStayStateSync"/> and reads the arrival back.
/// The no-show is queued by the check-in endpoints (E03/F01/US01/T04). The request of the host never waits for Octorate.
/// </summary>
/// <remarks>
/// Nothing is queued while <c>Features:OctorateConnector</c> is off or for a booking with no Octorate reservation; the job resolves the rest
/// (connection, confirmed mapping, writes not blocked) and writes nothing for a booking that is not mirrored. The org is the event's
/// (ADR-022-R6). A failure here is logged and never thrown: the check-in or the check-out is already saved.
/// </remarks>
public sealed class OctorateStayStateHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IBackgroundJobClient backgroundJobClient,
    ILogger<OctorateStayStateHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var state = domainEvent.Type switch
        {
            DomainEventType.CheckInCompleted => OctorateStayState.CheckInEnd,
            DomainEventType.CheckOutCompleted => OctorateStayState.CheckOutEnd,
            _ => (OctorateStayState?)null,
        };
        if (state is not { } stayState || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        try
        {
            var mirrored = await db.Bookings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .AnyAsync(b => b.Id == bookingId && b.OrgId == orgId && b.OctorateReservationId != null, ct);
            if (!mirrored)
                return;

            var jobId = backgroundJobClient.Enqueue<OctorateStayStateJob>(
                job => job.RunAsync(orgId, bookingId, stayState, CancellationToken.None));
            logger.LogInformation(
                "Booking {BookingId} of org {OrgId}: the check-in state {State} will be sent to Octorate (job {JobId})",
                bookingId, orgId, stayState, jobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The check-in state {State} of booking {BookingId} could not be queued for Octorate", stayState, bookingId);
        }
    }
}
