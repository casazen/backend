using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

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

    public static HostScope? Decide(UserAuthorizationSnapshot snapshot, string userId, IReadOnlySet<string> jwtRoles, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(jwtRoles);

        if (snapshot is { Exists: true, IsActive: false })
            return null;

        if (snapshot.OrgMember is { } member)
        {
            if (member.OrgId != orgId || member.Status != OrgMemberStatus.Active)
                return null;

            return member.Role switch
            {
                OrgRole.Owner or OrgRole.Admin or OrgRole.PropertyManager or OrgRole.Accountant => new HostScope(orgId),

                OrgRole.Collaborator => member.PropertyScope == PropertyScope.Selected
                    ? new HostScope(orgId, GrantedToUserId: userId, GrantedMode: member.GrantedMode)
                    : new HostScope(orgId),

                _ => null,
            };
        }

        // No org team: the token decides, as it always did (org-wide role, otherwise the properties the user created).
        return jwtRoles.Overlaps(HostRoles.OrgWide) ? new HostScope(orgId) : new HostScope(orgId, OwnerId: userId);
    }

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

        return propertyId is { } id && snapshot.OrgMember?.GrantedPropertyIds?.Contains(id) == true;
    }
}
