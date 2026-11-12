using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>Error codes of the owner portal operations shared by its services (E07/F07/US01).</summary>
public static class OwnerPortalErrors
{
    /// <summary>403: the caller holds the permission nowhere (a delegated contact the owner did not give it to).</summary>
    public const string PermissionMissing = "owner_portal_permission_missing";

    /// <summary>422: an explicit list of permissions that is empty. An empty list never means «all of them».</summary>
    public const string PermissionsEmpty = "owner_portal_permissions_empty";

    /// <summary>422: a permission that is not one of <see cref="OwnerPortalPermissions.All"/>.</summary>
    public const string PermissionUnknown = "owner_portal_permission_unknown";

    /// <summary>404: the property is not one the caller reaches with that permission now (another owner's, an ended ownership, missing: one answer).</summary>
    public const string PropertyNotFound = "owner_portal_property_not_found";

    public const string PermissionMissingMessageKey = "OwnerPortalPermissionMissing";
    public const string PermissionsEmptyMessageKey = "OwnerPortalPermissionsEmpty";
    public const string PermissionUnknownMessageKey = "OwnerPortalPermissionUnknown";
    public const string PropertyNotFoundMessageKey = "OwnerPortalPropertyNotFound";
}

/// <summary>
/// What an item of the owner portal operations is, in the <c>part</c> detail of its lines of the activity log (<c>OrgActivityType.OwnerPortalChanged</c>):
/// the subject id of the line is the id of that item. A closed list of codes.
/// </summary>
public static class OwnerPortalParts
{
    /// <summary>An owner stay (<c>OwnerStayRequest.Id</c>).</summary>
    public const string Stay = "stay";

    /// <summary>A rule of the owner stays (<c>OwnerStayRule.Id</c>).</summary>
    public const string StayRule = "stayRule";
}

/// <summary>What happened to an item of the owner portal operations, in the <c>status</c> detail of its lines of the activity log. A closed list of codes.</summary>
public static class OwnerPortalAuditStatuses
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string AutoApproved = "autoApproved";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Cancelled = "cancelled";
    public const string SyncAcknowledged = "syncAcknowledged";
    public const string SyncFailed = "syncFailed";
    public const string SyncRetried = "syncRetried";
    public const string Released = "released";
    public const string ReleaseAcknowledged = "releaseAcknowledged";
    public const string ReleaseFailed = "releaseFailed";
    public const string Saved = "saved";
    public const string Removed = "removed";
}

/// <summary>The codes of the permissions of the owner portal (AC-NF111-4), as the API speaks them.</summary>
public static class OwnerPortalPermissions
{
    public const string CalendarRead = "calendar.read";
    public const string StaysRequest = "stays.request";
    public const string AccountingRead = "accounting.read";
    public const string DocumentsRead = "documents.read";
    public const string InboxUse = "inbox.use";

    /// <summary>Every permission, in the order of the brief.</summary>
    public static IReadOnlyList<string> All { get; } = [CalendarRead, StaysRequest, AccountingRead, DocumentsRead, InboxUse];

    /// <summary>Every permission at once: what the owner holds on its own ownerships.</summary>
    public const OwnerPortalPermission Everything =
        OwnerPortalPermission.CalendarRead | OwnerPortalPermission.StaysRequest | OwnerPortalPermission.AccountingRead
        | OwnerPortalPermission.DocumentsRead | OwnerPortalPermission.InboxUse;

    /// <summary>The mask of a list of codes (case-insensitive, repeats allowed).</summary>
    /// <exception cref="DomainRuleException">
    /// <see cref="OwnerPortalErrors.PermissionsEmpty"/> for a missing or empty list (never «all»), <see cref="OwnerPortalErrors.PermissionUnknown"/> for a code
    /// that is not a permission.
    /// </exception>
    public static OwnerPortalPermission Parse(IReadOnlyCollection<string>? codes)
    {
        if (codes is null || codes.Count == 0)
            throw new DomainRuleException(OwnerPortalErrors.PermissionsEmpty, OwnerPortalErrors.PermissionsEmptyMessageKey);

        var mask = OwnerPortalPermission.None;
        foreach (var code in codes)
        {
            mask |= (code ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                CalendarRead => OwnerPortalPermission.CalendarRead,
                StaysRequest => OwnerPortalPermission.StaysRequest,
                AccountingRead => OwnerPortalPermission.AccountingRead,
                DocumentsRead => OwnerPortalPermission.DocumentsRead,
                InboxUse => OwnerPortalPermission.InboxUse,
                _ => throw new DomainRuleException(OwnerPortalErrors.PermissionUnknown, OwnerPortalErrors.PermissionUnknownMessageKey),
            };
        }

        return mask;
    }

    /// <summary>The codes of a mask, in the order of <see cref="All"/>.</summary>
    public static IReadOnlyList<string> CodesOf(OwnerPortalPermission permissions)
    {
        var codes = new List<string>();
        if (permissions.HasFlag(OwnerPortalPermission.CalendarRead))
            codes.Add(CalendarRead);
        if (permissions.HasFlag(OwnerPortalPermission.StaysRequest))
            codes.Add(StaysRequest);
        if (permissions.HasFlag(OwnerPortalPermission.AccountingRead))
            codes.Add(AccountingRead);
        if (permissions.HasFlag(OwnerPortalPermission.DocumentsRead))
            codes.Add(DocumentsRead);
        if (permissions.HasFlag(OwnerPortalPermission.InboxUse))
            codes.Add(InboxUse);
        return codes;
    }
}

/// <summary>
/// One property the caller of the owner portal reaches now, through one ownership in force (ADR-011-R2: started, not ended): its own, or the one of the
/// owner that delegated it (<see cref="ContactId"/>), with what it may do there.
/// </summary>
/// <param name="OrgId">The agency.</param>
/// <param name="PropertyOwnerId">The owner profile of that agency the ownership is of (<c>PropertyOwner.Id</c>).</param>
/// <param name="PropertyId">The property.</param>
/// <param name="OwnershipStart">When the ownership started (UTC).</param>
/// <param name="OwnershipEnd">When it ends (UTC); <c>null</c> while open.</param>
/// <param name="Permissions">Every permission for the owner; the ones the owner gave for a contact.</param>
/// <param name="ContactId">The delegation (<c>OwnerPortalContact.Id</c>) the grant comes from; <c>null</c> for the owner's own ownership.</param>
public sealed record OwnerPortalGrant(
    Guid OrgId,
    Guid PropertyOwnerId,
    Guid PropertyId,
    DateTime OwnershipStart,
    DateTime? OwnershipEnd,
    OwnerPortalPermission Permissions,
    Guid? ContactId);

/// <summary>
/// What the caller of the owner portal reaches at the moment of the request (E07/F07/US01, ADR-011-R2, AC-NF111-4): every read and every write of the
/// owner portal operations is judged against it, and it is resolved again at every request, so an ownership that ended, a removed owner or a withdrawn
/// delegation closes the access at the next call.
/// </summary>
public sealed class OwnerPortalAccess
{
    public OwnerPortalAccess(Guid ownerUserId, IReadOnlyList<OwnerPortalGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        OwnerUserId = ownerUserId;
        Grants = grants;
    }

    /// <summary>The person of the portal (<c>OwnerUser.Id</c>): an owner, a delegated contact, or both.</summary>
    public Guid OwnerUserId { get; }

    /// <summary>Every property reached, once per ownership and delegation.</summary>
    public IReadOnlyList<OwnerPortalGrant> Grants { get; }

    /// <summary>The actor of what the caller does, as the audit writes it: <c>owner:{OwnerUser.Id}</c>, never a name.</summary>
    public string Actor => ActorOf(OwnerUserId);

    /// <summary>The actor of a person of the portal: <c>owner:{OwnerUser.Id}</c>.</summary>
    public static string ActorOf(Guid ownerUserId) => $"owner:{ownerUserId:D}";

    /// <summary>The grants that carry <paramref name="permission"/>.</summary>
    public IReadOnlyList<OwnerPortalGrant> With(OwnerPortalPermission permission) =>
        Grants.Where(g => g.Permissions.HasFlag(permission)).ToList();

    /// <summary>The grants on <paramref name="propertyId"/> that carry <paramref name="permission"/>; the owner's own first.</summary>
    public IReadOnlyList<OwnerPortalGrant> On(Guid propertyId, OwnerPortalPermission permission) =>
        Grants.Where(g => g.PropertyId == propertyId && g.Permissions.HasFlag(permission))
            .OrderBy(g => g.ContactId is null ? 0 : 1)
            .ToList();

    /// <summary>True when the caller holds <paramref name="permission"/> on some property of the owner profile <paramref name="propertyOwnerId"/> of <paramref name="orgId"/>.</summary>
    public bool Covers(Guid orgId, Guid propertyOwnerId, OwnerPortalPermission permission) =>
        Grants.Any(g => g.OrgId == orgId && g.PropertyOwnerId == propertyOwnerId && g.Permissions.HasFlag(permission));

    /// <summary>Refuses a caller that holds <paramref name="permission"/> nowhere.</summary>
    /// <exception cref="DomainForbiddenException"><see cref="OwnerPortalErrors.PermissionMissing"/>.</exception>
    public void Require(OwnerPortalPermission permission)
    {
        if (!Grants.Any(g => g.Permissions.HasFlag(permission)))
            throw new DomainForbiddenException(OwnerPortalErrors.PermissionMissing, OwnerPortalErrors.PermissionMissingMessageKey);
    }

    /// <summary>The first grant on <paramref name="propertyId"/> with <paramref name="permission"/> (the owner's own first).</summary>
    /// <exception cref="DomainForbiddenException">The caller holds the permission nowhere.</exception>
    /// <exception cref="NotFoundException"><see cref="OwnerPortalErrors.PropertyNotFound"/>: not a property the caller reaches with it now.</exception>
    public OwnerPortalGrant RequireOn(Guid propertyId, OwnerPortalPermission permission)
    {
        Require(permission);
        return On(propertyId, permission).FirstOrDefault()
            ?? throw new NotFoundException($"Property {propertyId} not found")
            {
                Code = OwnerPortalErrors.PropertyNotFound,
                MessageKey = OwnerPortalErrors.PropertyNotFoundMessageKey,
            };
    }
}

/// <summary>
/// Resolves what the caller of the owner portal reaches (E07/F07/US01). The caller has no org, so the reads ignore the tenant filter of the request (which
/// would be fail-closed) and name every org, owner and property explicitly.
/// </summary>
public interface IOwnerPortalAccessResolver
{
    /// <summary>
    /// The grants of <paramref name="ownerUserId"/> now: every ownership of the account in force (started, not ended) on a property of the same org that
    /// is not deleted, of an owner profile that is not anonymized, with every permission; and every delegation that is active, with the permissions the
    /// owner gave, on the ownerships in force of the owner that gave it. Empty for nobody.
    /// </summary>
    Task<OwnerPortalAccess> ResolveAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
