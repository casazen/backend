using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
public sealed class HostResourceLookup(AppDbContext db) : IHostResourceLookup
{
    public Task<HostResource?> ForPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new HostResource(p.OrgId, p.OwnerId))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<HostResource?> ForBookingAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new HostResource(b.Property.OrgId, b.Property.OwnerId))
            .FirstOrDefaultAsync(cancellationToken);
}
