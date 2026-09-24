using Casazen.Core.Entities;

namespace Casazen.Core.Authorization;

/// <summary>
/// A host-side row as seen by resource-based authorization (TN-3): the tenant it belongs to and, for rows bound to a
/// property (the property itself, its bookings, payments, pricing, service requests), the owner of that property.
/// </summary>
/// <remarks>
/// The web layer passes it to <c>IAuthorizationService.AuthorizeAsync(User, resource, operation)</c>; the handler grants
/// the operation when the caller belongs to <see cref="OrgId"/>, holds the operation's context permission and, when
/// <see cref="PropertyOwnerId"/> is set, owns the property or has an org-wide role (<see cref="HostRoles.OrgWide"/>).
/// </remarks>
/// <param name="OrgId">Tenant of the row.</param>
/// <param name="PropertyOwnerId">Owner of the property the row is bound to; <c>null</c> for org-level rows (guests).</param>
public sealed record HostResource(Guid OrgId, string? PropertyOwnerId)
{
    /// <summary>The property itself, or any row whose access follows its property.</summary>
    public static HostResource ForProperty(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new HostResource(property.OrgId, property.OwnerId);
    }

    /// <summary>An org-level row that is not bound to one property (e.g. a guest of the org).</summary>
    public static HostResource ForOrg(Guid orgId) => new(orgId, null);
}
