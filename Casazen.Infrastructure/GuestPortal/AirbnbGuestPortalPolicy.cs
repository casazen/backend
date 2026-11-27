using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class AirbnbGuestPortalPolicy(AppDbContext db) : IGuestPortalPolicy
{
    public async Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken = default)
    {
        if (context.Source != BookingSource.Airbnb)
            return true;

        var json = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.Id == context.PropertyId && p.OrgId == context.OrgId)
            .Select(p => p.GuestPortalSetupJson).SingleOrDefaultAsync(cancellationToken);
        return !GuestPortalSetup.Read(json).HidePaidServicesOnAirbnb;
    }
}
