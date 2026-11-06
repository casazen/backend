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

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The arrival and the departure of a stay, which only CasaZen knows (Octorate sends no event for them), reach Octorate (E02/F02/US01/T02,
/// ADR-031-R10): when the host registers the arrival (<see cref="DomainEventType.CheckInCompleted"/>) or checks the stay out, through the
/// check-out wizard or not (<see cref="DomainEventType.CheckOutCompleted"/>), the booking is already changed and its event already written to
/// the outbox in the same save; this handler, run by the dispatcher, queues the job that sends the new state
/// (<see cref="OctorateBookingStatePushJob"/> and <see cref="IBookingImportService.SyncBookingStateAsync"/>), so the request of the host never waits
/// for Octorate and a failure of Octorate never fails the check-out.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what exists in Octorate.</b> A booking with no Octorate reservation, and every event while <c>Features:OctorateConnector</c> is off,
/// queue nothing.</item>
/// <item><b>No echo.</b> Only these two events of CasaZen's own states are handled; the events recorded by the import
/// (<c>BookingCreated</c>, <c>BookingConfirmed</c>, <c>BookingModified</c>, <c>BookingCancelled</c> with <c>Origin = Octorate</c>) queue nothing.</item>
/// <item>At least once: a delivered-again event queues a second push of the same state, which Octorate takes as unchanged (304).</item>
/// <item>The org is the event's (ADR-022-R6): the booking is read in it.</item>
/// </list>
/// </remarks>
public sealed class OctorateBookingStatePushHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IBackgroundJobClient backgroundJobClient,
    ILogger<OctorateBookingStatePushHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var status = domainEvent.Type switch
        {
            DomainEventType.CheckInCompleted => BookingStatus.CheckedIn,
            DomainEventType.CheckOutCompleted => BookingStatus.CheckedOut,
            _ => (BookingStatus?)null,
        };
        if (status is not { } newStatus || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var mirrored = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(b => b.Id == bookingId && b.OrgId == orgId && b.OctorateReservationId != null, ct);
        if (!mirrored)
            return;

        var jobId = backgroundJobClient.Enqueue<OctorateBookingStatePushJob>(job => job.PushAsync(orgId, bookingId, newStatus, CancellationToken.None));
        logger.LogInformation("Booking {BookingId} of org {OrgId}: the state {Status} will be sent to Octorate (job {JobId})", bookingId, orgId, newStatus, jobId);
    }
}
