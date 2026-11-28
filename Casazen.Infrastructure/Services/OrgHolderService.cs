using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class OrgHolderService(AppDbContext db) : IOrgHolderService
{
    public async Task<bool> IsHolderAsync(
        string userId,
        Guid orgId,
        string propertyCreatorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var member = await db.OrgMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.OrgId, m.Role, m.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (member is null)
            return string.Equals(propertyCreatorId, userId, StringComparison.Ordinal);

        return member.OrgId == orgId
               && member.Status == OrgMemberStatus.Active
               && member.Role is OrgRole.Owner or OrgRole.Admin;
    }
}
