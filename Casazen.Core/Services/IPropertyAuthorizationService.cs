using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Whether a user reaches a property: the check of the controllers not yet moved to the resource handler
/// (<c>HostResourceAuthorizationHandler</c>, TN-3). It asks <see cref="Casazen.Core.Authorization.IHostScopeResolver"/>, the one
/// answer to «which properties?» (AM-03): the org role of a member, the properties given to a collaborator «Solo alcuni», or,
/// for an account in no org team, the old rule (an org-wide token role, or the properties it created).
/// </summary>
public interface IPropertyAuthorizationService
{
    /// <summary>True when <paramref name="userId"/> reaches <paramref name="property"/>.</summary>
    /// <param name="userRoles">The roles of the token: only an account with no org membership is decided by them.</param>
    Task<bool> CanAccessAsync(string userId, Property property, IEnumerable<string> userRoles);

    /// <summary>
    /// As <see cref="CanAccessAsync"/> for a property known by its id. A property that does not exist, or is not in the
    /// caller's org (the tenant filter), is not reachable.
    /// </summary>
    Task<bool> CanAccessPropertyAsync(string userId, Guid propertyId, IEnumerable<string> userRoles);
}
