using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reach check still used by the controllers not yet moved to the resource handler (<c>HostResourceAuthorizationHandler</c>,
/// TN-3). New code authorizes with <c>IAuthorizationService</c> and a <see cref="HostResource"/>; never call this with a
/// hand-written role list. The decision is the scope resolver's (AM-03), not a list of token roles.
/// </summary>
public class PropertyAuthorizationService(
    IPropertyRepository propertyRepository,
    IHostScopeResolver scopeResolver) : IPropertyAuthorizationService
{
    public Task<bool> CanAccessAsync(string userId, Property property, IEnumerable<string> userRoles)
    {
        ArgumentNullException.ThrowIfNull(property);
        return scopeResolver.CanReachPropertyAsync(
            userId, ToSet(userRoles), property.OrgId, property.Id, property.OwnerId);
    }

    public async Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles)
    {
        // The property row alone: never its bookings and OTA integrations just to read the owner (A2-17).
        var property = await propertyRepository.GetRecordAsync(propertyId);
        return property is not null && await CanAccessAsync(userId, property, userRoles);
    }

    private static IReadOnlySet<string> ToSet(IEnumerable<string> roles) =>
        roles as IReadOnlySet<string> ?? new HashSet<string>(roles, StringComparer.Ordinal);
}
