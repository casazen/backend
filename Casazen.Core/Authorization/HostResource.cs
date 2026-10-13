using Casazen.Core.Entities;

namespace Casazen.Core.Authorization;

/// <summary>
/// A host-side row as seen by resource-based authorization (TN-3): the tenant it belongs to and, for rows bound to a
/// property (the property itself, its bookings, payments, pricing, service requests), the property and its creator.
/// </summary>
/// <remarks>
/// The web layer passes it to <c>IAuthorizationService.AuthorizeAsync(User, resource, operation)</c>; the handler grants
/// the operation when the caller belongs to <see cref="OrgId"/>, holds the operation's context permission and, when the row
/// is bound to a property, reaches it: its org role says every property of the org, or the ones it was given
/// (<see cref="PropertyId"/>), or, for an account that is in no org team, the ones it created
/// (<see cref="PropertyOwnerId"/>). See <see cref="IHostScopeResolver"/>.
/// </remarks>
/// <param name="OrgId">Tenant of the row.</param>
/// <param name="PropertyOwnerId">Creator of the property the row is bound to; <c>null</c> for org-level rows (guests).</param>
/// <param name="PropertyId">
/// The property the row is bound to (AM-03). It decides for a member «Solo alcuni»: a bound row whose property is not
/// known is not granted to one (fail closed). <c>null</c> for org-level rows.
/// </param>
public sealed record HostResource(Guid OrgId, string? PropertyOwnerId, Guid? PropertyId = null)
{
    /// <summary>True when the row follows a property (and so its reach is decided by the caller's scope).</summary>
    public bool IsBoundToProperty => PropertyOwnerId is not null || PropertyId is not null;

    /// <summary>The property itself, or any row whose access follows its property.</summary>
    public static HostResource ForProperty(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new HostResource(property.OrgId, property.OwnerId, property.Id);
    }

    /// <summary>An org-level row that is not bound to one property (e.g. a guest of the org).</summary>
    public static HostResource ForOrg(Guid orgId) => new(orgId, null);
}
