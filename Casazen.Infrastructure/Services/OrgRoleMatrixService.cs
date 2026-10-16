using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgRoleMatrixService" />
/// <remarks>
/// <c>Roles</c> and <c>RolePermissions</c> are global reference data (not tenant data): one query reads the roles of the three
/// contexts of the team with their permissions, and <see cref="OrgRoleMatrix"/> does the rest in memory. The only thing that
/// depends on the org is the caller's own member row, read with the org passed in (the caller's, never a value of the request).
/// </remarks>
public sealed class OrgRoleMatrixService(AppDbContext db) : IOrgRoleMatrixService
{
    public async Task<IReadOnlyList<OrgRoleView>> GetAsync(
        Guid orgId,
        string callerUserId,
        CancellationToken cancellationToken = default)
    {
        var caller = await OrgTeamAccess.RequireMemberAsync(db, orgId, callerUserId, cancellationToken);

        var contexts = OrgRoleMatrix.Contexts;
        var roles = await db.Roles.AsNoTracking()
            .Where(r => contexts.Contains(r.ContextKey))
            .Select(r => new { r.ContextKey, r.RoleKey, Permissions = r.Permissions.Select(p => p.PermissionKey).ToList() })
            .ToListAsync(cancellationToken);

        var seeded = roles.ToDictionary(
            r => (r.ContextKey, r.RoleKey),
            r => (IReadOnlyCollection<string>)r.Permissions);

        return OrgRoleMatrix.Build(seeded, caller.Role);
    }
}
