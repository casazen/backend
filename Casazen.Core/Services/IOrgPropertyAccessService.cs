using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>One property of the org as the access page shows it for a member (AM-03).</summary>
/// <param name="Granted">The member reaches it: every property for a member with every property, the given ones otherwise.</param>
/// <param name="PeopleWithAccess">
/// «Chi può accedere»: the active members who reach it. Every member with every property counts, and so does every
/// collaborator «Solo alcuni» who was given it; the deactivated do not.
/// </param>
public sealed record PropertyAccessItem(Guid PropertyId, string Name, string City, bool Granted, int PeopleWithAccess);

/// <summary>The access of a member to the properties of its org.</summary>
/// <param name="MemberId">The member row (<c>OrgMember.Id</c>), what the endpoints address.</param>
/// <param name="ScopeSupported">
/// Whether the member can be limited to some properties: only a collaborator can (the others reach every property of the
/// org). When it is <c>false</c> every item is granted.
/// </param>
public sealed record OrgMemberPropertyAccessView(
    Guid MemberId,
    OrgRole Role,
    PropertyScope PropertyScope,
    bool ScopeSupported,
    IReadOnlyList<PropertyAccessItem> Properties);

/// <summary>
/// Which properties each member of the org reaches, and who is in charge of each property (AM-03): the writes of
/// <see cref="Casazen.Core.Entities.PropertyMemberAccess"/> and of <see cref="Casazen.Core.Entities.Property.ResponsibleUserId"/>.
/// The reads that decide are not here: the lists say <c>EXISTS</c> in SQL (<c>InScope</c>) and the single checks ask
/// <see cref="Casazen.Core.Authorization.IHostScopeResolver"/>.
/// </summary>
/// <remarks>
/// <para>Every write takes the org's people lock (the one the role changes take), reads the actor and the member under it,
/// writes the scope of the member and its rows in one <c>SaveChanges</c> and invalidates the member's authorization cache after
/// the commit: this instance sees the change at once, the others within the cache duration (60 s). The lists read the grants
/// themselves in SQL, so what may be late on the other instances is the decision whether the member is limited at all.</para>
/// </remarks>
public interface IOrgPropertyAccessService
{
    /// <summary>The properties of the org with, for <paramref name="memberId"/>, whether it reaches each one, and how many people do.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>org_member_not_found</c>.</exception>
    Task<OrgMemberPropertyAccessView> GetAsync(Guid orgId, Guid memberId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the scope of the member: <see cref="PropertyScope.All"/> (every property, the ones added later too; the rows go) or
    /// <see cref="PropertyScope.Selected"/> with exactly <paramref name="propertyIds"/> (none = it sees nothing). The ids are
    /// ignored for <see cref="PropertyScope.All"/>.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>org_member_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><c>org_owner_required</c>: only the owner touches an administrator.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <see cref="OrgMembershipErrors.ScopeNotSupported"/> (a member who is not a collaborator cannot be limited) or
    /// <see cref="OrgMembershipErrors.PropertyUnknown"/> (an id that is not a property of the org).
    /// </exception>
    Task<OrgMemberPropertyAccessView> SetAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        PropertyScope scope,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts <paramref name="responsibleUserId"/> in charge of the property (or nobody, <c>null</c>): the person who is told, with
    /// the org's administrators, when something happens on it. It must be an active member of the org who reaches the property.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property is not in the org.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><see cref="PropertyResponsibleErrors.Invalid"/>.</exception>
    Task SetResponsibleAsync(
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable codes of <see cref="IOrgPropertyAccessService.SetResponsibleAsync"/> (422), translated by the clients.</summary>
public static class PropertyResponsibleErrors
{
    /// <summary>422: the person in charge is not an active member of the org who reaches the property.</summary>
    public const string Invalid = "property_responsible_invalid";
}
