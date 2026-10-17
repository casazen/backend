using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class GuestAccessService(AppDbContext dbContext) : IGuestAccessService
{
    public Task<bool> IsGuestAccessibleAsync(Guid guestId, HostScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return dbContext.Guests
            .AsNoTracking()
            .Where(g => g.Id == guestId && g.OrgId == scope.OrgId)
            .InScope(scope, dbContext.Bookings)
            .AnyAsync(cancellationToken);
    }
}
