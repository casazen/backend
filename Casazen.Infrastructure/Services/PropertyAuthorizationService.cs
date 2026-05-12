using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class PropertyAuthorizationService(IPropertyRepository propertyRepository) : IPropertyAuthorizationService
{
    private static readonly HashSet<string> PrivilegedRoles = ["PropertyManager", "Admin"];

    public bool CanAccess(string userId, string propertyOwnerId, IEnumerable<string> userRoles)
        => userRoles.Any(r => PrivilegedRoles.Contains(r)) || propertyOwnerId == userId;

    public async Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles)
    {
        if (userRoles.Any(r => PrivilegedRoles.Contains(r))) return true;
        var property = await propertyRepository.GetByIdAsync(propertyId);
        return property != null && property.OwnerId == userId;
    }
}
