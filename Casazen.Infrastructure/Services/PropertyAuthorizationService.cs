using Casazen.Core.Authorization;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Legacy owner-or-org-wide-role check still used by the controllers not yet moved to the resource handler
/// (<c>HostResourceAuthorizationHandler</c>, TN-3). New code authorizes with <c>IAuthorizationService</c> and a
/// <see cref="HostResource"/>; never call this with a hand-written role list.
/// </summary>
public class PropertyAuthorizationService(IPropertyRepository propertyRepository) : IPropertyAuthorizationService
{
    public bool CanAccess(string userId, string propertyOwnerId, IEnumerable<string> userRoles)
        => userRoles.Any(HostRoles.OrgWide.Contains) || propertyOwnerId == userId;

    public async Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles)
    {
        if (userRoles.Any(HostRoles.OrgWide.Contains)) return true;
        var property = await propertyRepository.GetByIdAsync(propertyId);
        return property != null && property.OwnerId == userId;
    }
}
