using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
public sealed class UserContextMembershipService(
    AppDbContext db,
    IUserAuthorizationCache authorizationCache,
    ILogger<UserContextMembershipService> logger) : IUserContextMembershipService
{
    public async Task GrantAsync(string userId, IEnumerable<UserRole> roles, CancellationToken cancellationToken = default)
    {
        var targets = MapToContexts(roles);
        if (targets.Count == 0)
            return;

        var contextKeys = targets.Select(t => t.ContextKey).ToList();
        var roleRows = await db.Roles
            .AsNoTracking()
            .Where(r => contextKeys.Contains(r.ContextKey))
            .Select(r => new RoleRow(r.Id, r.ContextKey, r.RoleKey))
            .ToListAsync(cancellationToken);

        try
        {
            await ApplyGrantsAsync(userId, targets, contextKeys, roleRows, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request of the same user (e.g. the onboarding submitted twice) inserted a membership first.
            // TN-4: drop the rejected inserts, re-read and apply again; the second pass only updates existing rows.
            foreach (var pending in db.ChangeTracker.Entries<UserContextMembership>()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                pending.State = EntityState.Detached;
            }

            logger.LogInformation(
                "Context membership of user {UserId} inserted by a parallel request, re-applying the grant", userId);
            await ApplyGrantsAsync(userId, targets, contextKeys, roleRows, cancellationToken);
        }

        authorizationCache.Invalidate(userId);
    }

    private async Task ApplyGrantsAsync(
        string userId,
        IReadOnlyList<BootstrapContextMembership> targets,
        List<string> contextKeys,
        List<RoleRow> roleRows,
        CancellationToken cancellationToken)
    {
        var existing = await db.UserContextMemberships
            .Where(m => m.UserId == userId && contextKeys.Contains(m.ContextKey))
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var target in targets)
        {
            var role = roleRows.FirstOrDefault(r =>
                string.Equals(r.ContextKey, target.ContextKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RoleKey, target.RoleKey, StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                // e.g. the supplier context, which is derived from User.SupplierOrgId instead of a DB membership.
                logger.LogDebug(
                    "No DB role {RoleKey} for context {ContextKey}; membership not written for user {UserId}",
                    target.RoleKey, target.ContextKey, userId);
                continue;
            }

            var membership = existing.FirstOrDefault(m =>
                string.Equals(m.ContextKey, target.ContextKey, StringComparison.OrdinalIgnoreCase));
            if (membership is null)
            {
                db.UserContextMemberships.Add(new UserContextMembership
                {
                    UserId = userId,
                    ContextKey = role.ContextKey,
                    RoleId = role.Id,
                });
                changed = true;
            }
            else if (membership.RoleId != role.Id)
            {
                membership.RoleId = role.Id;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Context memberships granted for user {UserId}: [{Contexts}]",
                userId, string.Join(", ", contextKeys));
        }
    }

    public async Task RevokeAsync(string userId, IEnumerable<UserRole> roles, CancellationToken cancellationToken = default)
    {
        var contextKeys = MapToContexts(roles)
            .Select(t => t.ContextKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (contextKeys.Count == 0)
            return;

        var memberships = await db.UserContextMemberships
            .Where(m => m.UserId == userId && contextKeys.Contains(m.ContextKey))
            .ToListAsync(cancellationToken);

        if (memberships.Count > 0)
        {
            db.UserContextMemberships.RemoveRange(memberships);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Context memberships revoked for user {UserId}: [{Contexts}]",
                userId, string.Join(", ", memberships.Select(m => m.ContextKey)));
        }

        authorizationCache.Invalidate(userId);
    }

    private sealed record RoleRow(int Id, string ContextKey, string RoleKey);

    private static IReadOnlyList<BootstrapContextMembership> MapToContexts(IEnumerable<UserRole> roles) =>
        ContextAccessBootstrap.DeriveContextsFromJwtRoles(roles.Distinct().Select(r => r.ToString()));
}
