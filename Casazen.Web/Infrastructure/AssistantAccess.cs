using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

internal static class AssistantAccess
{
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
