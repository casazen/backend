using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IBookingOctorateSyncReader"/> (E08/F03/US03/T02, NF-74, NF-77): the row of <see cref="OctorateBookingSync"/> of one booking, as the
/// booking answer says it. A read only: it changes nothing and calls nobody.
/// </summary>
/// <remarks>
/// <para><b>Tenant.</b> The row is asked for by the booking and the org together, on top of the tenant filter of the request, so a booking of another
/// org never shows a state of its own.</para>
/// <para><b>Retry.</b> <see cref="BookingSyncStatus.CanRetry"/> is the rule of the retry endpoint (<see cref="OctorateBookingSync.IsRetryable"/>: a push that
/// failed or was refused) and that the connector is on, because that endpoint answers 404 while it is off. The permission of the caller is the web layer's.</para>
/// <para><b>Conflict.</b> What Octorate holds is given only in the <see cref="OctorateSyncState.Conflict"/> state: in any other the stored dates are
/// stale, and a screen that compared with them would show a difference that is not there.</para>
/// </remarks>
public sealed class BookingOctorateSyncReader(AppDbContext db, IFeatureFlags featureFlags) : IBookingOctorateSyncReader
{
    public async Task<BookingSyncStatus?> GetAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        var row = await db.OctorateBookingSyncs
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId && s.OrgId == orgId)
            .Select(s => new { s.State, s.LastOperation, s.LastSyncedAt, s.ErrorCode, s.OctorateVersionJson })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        return new BookingSyncStatus(
            row.State,
            row.LastOperation,
            row.LastSyncedAt,
            row.ErrorCode,
            OctorateBookingSync.IsRetryable(row.State) && featureFlags.IsEnabled(FeatureFlags.OctorateConnector),
            row.State == OctorateSyncState.Conflict ? OctorateVersionSnapshot.Parse(row.OctorateVersionJson) : null);
    }
}
