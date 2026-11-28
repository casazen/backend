using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public static class OwnerPortalErrors
{
    public const string PermissionMissing = "owner_portal_permission_missing";

    public const string PermissionsEmpty = "owner_portal_permissions_empty";

    public const string PermissionUnknown = "owner_portal_permission_unknown";

    public const string PropertyNotFound = "owner_portal_property_not_found";

    public const string PermissionMissingMessageKey = "OwnerPortalPermissionMissing";
    public const string PermissionsEmptyMessageKey = "OwnerPortalPermissionsEmpty";
    public const string PermissionUnknownMessageKey = "OwnerPortalPermissionUnknown";
    public const string PropertyNotFoundMessageKey = "OwnerPortalPropertyNotFound";
}

public static class OwnerPortalParts
{
    public const string Stay = "stay";

    public const string StayRule = "stayRule";

    public const string Document = "document";

    public const string Contact = "contact";

    public const string Conversation = "conversation";
}

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
    public const string Shared = "shared";
    public const string Versioned = "versioned";
    public const string Revoked = "revoked";
    public const string Restored = "restored";
    public const string Invited = "invited";
    public const string Accepted = "accepted";
    public const string PermissionsChanged = "permissionsChanged";
    public const string Opened = "opened";
}

public static class OwnerPortalPermissions
{
    public const string CalendarRead = "calendar.read";
    public const string StaysRequest = "stays.request";
    public const string AccountingRead = "accounting.read";
    public const string DocumentsRead = "documents.read";
    public const string InboxUse = "inbox.use";

    public static IReadOnlyList<string> All { get; } = [CalendarRead, StaysRequest, AccountingRead, DocumentsRead, InboxUse];

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

public sealed class OwnerPortalAccess
{
    public OwnerPortalAccess(Guid ownerUserId, IReadOnlyList<OwnerPortalGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        OwnerUserId = ownerUserId;
        Grants = grants;
    }

    public Guid OwnerUserId { get; }

    public IReadOnlyList<OwnerPortalGrant> Grants { get; }

    public string Actor => ActorOf(OwnerUserId);

    public static string ActorOf(Guid ownerUserId) => $"owner:{ownerUserId:D}";

    public IReadOnlyList<OwnerPortalGrant> With(OwnerPortalPermission permission) =>
        Grants.Where(g => g.Permissions.HasFlag(permission)).ToList();

    public IReadOnlyList<OwnerPortalGrant> On(Guid propertyId, OwnerPortalPermission permission) =>
        Grants.Where(g => g.PropertyId == propertyId && g.Permissions.HasFlag(permission))
            .OrderBy(g => g.ContactId is null ? 0 : 1)
            .ToList();

    public bool Covers(Guid orgId, Guid propertyOwnerId, OwnerPortalPermission permission) =>
        Grants.Any(g => g.OrgId == orgId && g.PropertyOwnerId == propertyOwnerId && g.Permissions.HasFlag(permission));

    public void Require(OwnerPortalPermission permission)
    {
        if (!Grants.Any(g => g.Permissions.HasFlag(permission)))
            throw new DomainForbiddenException(OwnerPortalErrors.PermissionMissing, OwnerPortalErrors.PermissionMissingMessageKey);
    }

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
    Task<OwnerPortalAccess> ResolveAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
