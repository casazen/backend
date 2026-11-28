using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPropertyAuthorizationService
{
    Task<bool> CanAccessAsync(string userId, Property property, IEnumerable<string> userRoles);

    Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles);
}
