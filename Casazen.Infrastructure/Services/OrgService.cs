using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Read access to <see cref="Org"/> tenants (US-004). <see cref="Org"/> is not subject to the
/// tenant query filter, so these reads are explicit by id/slug/user and never widen tenant scope.
/// </summary>
public class OrgService(AppDbContext dbContext) : IOrgService
{
    public Task<Org?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<Org?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var orgId = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.OrgId)
            .FirstOrDefaultAsync(cancellationToken);

        return orgId is null
            ? null
            : await dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
    }

    public Task<Org?> GetPublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Slug == slug && o.IsActive, cancellationToken);
}
