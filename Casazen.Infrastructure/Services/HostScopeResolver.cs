using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostScopeResolver" />
/// <remarks>
/// Reads the authorization snapshot (<see cref="IUserAuthorizationSnapshotStore"/>: once per request, cached across them), so
/// deciding a scope costs no query of its own. The decision itself is <see cref="Decide"/>, a pure function.
/// </remarks>
public sealed class HostScopeResolver(
    IUserAuthorizationSnapshotStore snapshotStore,
    ILogger<HostScopeResolver> logger) : IHostScopeResolver
{
    public async Task<HostScope?> ResolveAsync(
        string userId,
        IReadOnlySet<string> jwtRoles,
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        var snapshot = await snapshotStore.GetAsync(userId, cancellationToken);
        var scope = Decide(snapshot, userId, jwtRoles, orgId);
        if (scope is null)
        {
            logger.LogDebug(
                "No host scope for user {UserId} in org {OrgId}: inactive account, deactivated member or member of another org",
                userId, orgId);
        }

        return scope;
    }

    public async Task<bool> CanReachPropertyAsync(
        string userId,
        IReadOnlySet<string> jwtRoles,
        Guid orgId,
        Guid? propertyId,
        string? propertyOwnerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var snapshot = await snapshotStore.GetAsync(userId, cancellationToken);
        var scope = Decide(snapshot, userId, jwtRoles, orgId);
        return scope is not null && Reaches(snapshot, scope, propertyId, propertyOwnerId);
    }

    /// <summary>
    /// The scope of the user (<c>null</c>: none), from its snapshot. A person with an <see cref="Casazen.Core.Entities.OrgMember"/>
    /// row is decided by that row alone, whatever the token says; a person with none keeps the rule of before the team.
    /// </summary>
    public static HostScope? Decide(UserAuthorizationSnapshot snapshot, string userId, IReadOnlySet<string> jwtRoles, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(jwtRoles);

        // A deactivated account reaches nothing. A user the database does not know (a test account, a token ahead of its
        // sync) is still decided by its token, like the context permissions (ContextAuthorizationService).
        if (snapshot is { Exists: true, IsActive: false })
            return null;

        if (snapshot.OrgMember is { } member)
        {
            // The member row is the source of truth: another org's member, or a deactivated one, reaches nothing here.
            if (member.OrgId != orgId || member.Status != OrgMemberStatus.Active)
                return null;

            return member.Role switch
            {
                // The owner, the administrators, the property managers and the accountants see every property of the org.
                OrgRole.Owner or OrgRole.Admin or OrgRole.PropertyManager or OrgRole.Accountant => new HostScope(orgId),

                // Only the collaborator can be limited to some properties; with none given it reaches none.
                OrgRole.Collaborator => member.PropertyScope == PropertyScope.Selected
                    ? new HostScope(orgId, GrantedToUserId: userId)
                    : new HostScope(orgId),

                // An unknown role is never widened.
                _ => null,
            };
        }

        // No org team: the token decides, as it always did (org-wide role, otherwise the properties the user created).
        return jwtRoles.Overlaps(HostRoles.OrgWide) ? new HostScope(orgId) : new HostScope(orgId, OwnerId: userId);
    }

    /// <summary>Whether the property of a row is inside <paramref name="scope"/>, decided from the snapshot (no query).</summary>
    public static bool Reaches(UserAuthorizationSnapshot snapshot, HostScope scope, Guid? propertyId, string? propertyOwnerId)
    {
        if (scope.IsOrgWide)
            return true;

        if (scope.OwnerId is { } ownerId)
        {
            if (!string.Equals(propertyOwnerId, ownerId, StringComparison.Ordinal))
                return false;

            if (scope.GrantedToUserId is null)
                return true;
        }

        // A member «Solo alcuni» reaches the properties it was given, and only the ones whose id is known.
        return propertyId is { } id && snapshot.OrgMember?.GrantedPropertyIds?.Contains(id) == true;
    }
}
