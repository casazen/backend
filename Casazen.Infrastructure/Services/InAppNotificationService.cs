using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IInAppNotificationService" />
/// <remarks>
/// <para><b>Whose rows.</b> <see cref="OwnNotifications"/> is the one place that says it: the rows of <c>UserId</c> whose org is
/// the org of the user (<c>User.OrgId</c>, the host org) or its supplier org (<c>User.SupplierOrgId</c>), read from the user's
/// row in the same statement. That is why the tenant filter is bypassed here: it knows the host org only (a supplier-only
/// account has none, and every row of it would be hidden), and a user who has left an org must not keep reading what happened
/// there. Every read, count and update goes through it, so no operation can reach a row of another user or another org.</para>
/// <para><b>Updates are single statements</b> (<c>ExecuteUpdate</c>), conditioned on <c>ReadAt IS NULL</c>: no tracked row to lose
/// a race on, nothing to fail if the retention deletes the row meanwhile, and a second call changes nothing.</para>
/// </remarks>
public sealed class InAppNotificationService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<InAppNotificationService> logger) : IInAppNotificationService
{
    public async Task<InAppNotificationPage> ListAsync(
        string userId,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, InAppNotificationLimits.MaxPageSize);

        var query = OwnNotifications(userId);
        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new InAppNotificationItem(n.Id, n.Type, n.EntityId, n.CreatedAt, n.ReadAt))
            .ToListAsync(cancellationToken);

        return new InAppNotificationPage(items, total, page, pageSize);
    }

    public async Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await OwnNotifications(userId).CountAsync(n => n.ReadAt == null, cancellationToken);
    }

    public async Task MarkReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = Now();
        var updated = await OwnNotifications(userId)
            .Where(n => n.Id == notificationId && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), cancellationToken);
        if (updated > 0)
            return;

        // Nothing changed: it was read already (fine, idempotent), or it is not the user's (404, the same answer as for an id
        // that never existed, so an id of somebody else tells nothing).
        if (!await OwnNotifications(userId).AnyAsync(n => n.Id == notificationId, cancellationToken))
            throw new NotFoundException($"In-app notification {notificationId} not found for the caller");
    }

    public async Task<int> MarkAllReadAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = Now();
        return await OwnNotifications(userId)
            .Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = Now().AddDays(-InAppNotificationLimits.RetentionDays);

        // IgnoreQueryFilters([Tenant]): the retention is a platform job without a tenant and deletes by age, whatever the org.
        var deleted = await db.InAppNotifications
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(n => n.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation(
                "Deleted {Deleted} in-app notifications older than {Cutoff:O} ({RetentionDays} days)",
                deleted,
                cutoff,
                InAppNotificationLimits.RetentionDays);
        }

        return deleted;
    }

    /// <summary>
    /// The rows of <paramref name="userId"/> in an org the user belongs to: its host org (<c>User.OrgId</c>) or its supplier org
    /// (<c>User.SupplierOrgId</c>). The tenant filter is bypassed on purpose and replaced by this predicate (see the remarks of the
    /// class); the user id is the caller's own, never an input of the request.
    /// </summary>
    internal IQueryable<InAppNotification> OwnNotifications(string userId) =>
        db.InAppNotifications
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(n => n.UserId == userId
                        && db.Users.Any(u => u.Id == userId && (u.OrgId == n.OrgId || u.SupplierOrgId == n.OrgId)));

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);
}
