namespace Casazen.Core.Services;

public interface IPropertyAuthorizationService
{
    bool CanAccess(string userId, string propertyOwnerId, IEnumerable<string> userRoles);
    Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles);
}
