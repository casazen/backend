using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class GuestAccessService(AppDbContext dbContext) : IGuestAccessService
{
    public Task<bool> IsGuestAccessibleAsync(Guid guestId, Guid orgId, CancellationToken cancellationToken = default) =>
        dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(b => b.GuestId == guestId && b.OrgId == orgId, cancellationToken);
}
