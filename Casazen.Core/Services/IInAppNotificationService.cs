using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The in-app notifications of a user, read and cleared by the user itself (UI-12a; runbook
/// <c>docs/runbooks/in-app-notifications.md</c>). Every operation acts on the rows of <c>userId</c> in the orgs the user
/// belongs to, and on no others: the id of a notification of another user, or of an org the user has left, is a
/// <see cref="NotFoundException"/> (404, never 403), exactly like an id that does not exist.
/// </summary>
/// <remarks>
/// The rows are written by <c>InAppNotificationJob</c>, not here. <c>userId</c> is always the caller's own (the token's
/// <c>sub</c>): it is never taken from a route or a body.
/// </remarks>
public interface IInAppNotificationService
{
    Task<InAppNotificationPage> ListAsync(
        string userId,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one notification as read. Idempotent: one already read stays as it is and the call succeeds. Raises
    /// <see cref="NotFoundException"/> when the user has no such notification (another user's, another org's, deleted by the
    /// retention, never existed).
    /// </summary>
    Task MarkReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks every unread notification of the user as read and returns how many it was. Idempotent.</summary>
    Task<int> MarkAllReadAsync(string userId, CancellationToken cancellationToken = default);

    Task<int> MarkEntityReadAsync(string userId, string type, Guid entityId, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public sealed record InAppNotificationItem(Guid Id, string Type, Guid? EntityId, DateTime CreatedAt, DateTime? ReadAt);

public sealed record InAppNotificationPage(IReadOnlyList<InAppNotificationItem> Items, int TotalCount, int Page, int PageSize);
