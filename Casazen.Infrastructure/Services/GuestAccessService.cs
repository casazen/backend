using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class GuestAccessService(AppDbContext dbContext) : IGuestAccessService
{
    public Task<bool> IsGuestAccessibleAsync(Guid guestId, Guid orgId, CancellationToken cancellationToken = default) =>
        dbContext.Guests
            .AsNoTracking()
            .AnyAsync(g => g.Id == guestId && g.OrgId == orgId, cancellationToken);
}
