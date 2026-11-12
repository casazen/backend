using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The row-level check of the property that the endpoints of the assistant (<c>api/assistant/properties/{propertyId}/…</c>, E05/F05/US01) share, the same one as the
/// mapping of the listings (<c>OctorateMappingController</c>) and the short-stay side of a property (<c>PropertiesController</c>, AM-03): the property is in the caller's
/// organization, not deleted, and within the caller's reach (the whole organization, or the properties a collaborator «Solo alcuni» was given), and the caller holds
/// the permission of the operation in the short-rent context. <b>A property that fails any of these is the same 404 as one that does not exist</b>, so the answer tells
/// nothing about properties the caller does not reach.
/// </summary>
internal static class AssistantAccess
{
    /// <summary>True when <paramref name="propertyId"/> is a property of <paramref name="orgId"/> the caller may use for <paramref name="operation"/>.</summary>
    public static async Task<bool> CanUsePropertyAsync(
        IPropertyService properties,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        Guid orgId,
        Guid propertyId,
        HostOperationRequirement operation)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(operation);

        var property = await properties.GetPropertyRecordAsync(propertyId);
        return property is not null
               && property.OrgId == orgId
               && await authorization.IsAuthorizedAsync(user, HostResource.ForProperty(property), operation);
    }
}
