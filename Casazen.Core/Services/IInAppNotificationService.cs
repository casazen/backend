using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The in-app notifications of a user, read and cleared by the user itself (UI-12a, behind <c>Features:InAppNotifications</c>;
/// runbook <c>docs/runbooks/in-app-notifications.md</c>). Every operation acts on the rows of <c>userId</c> in the orgs the user
/// belongs to, and on no others: the id of a notification of another user, or of an org the user has left, is a
/// <see cref="NotFoundException"/> (404, never 403), exactly like an id that does not exist.
/// </summary>
/// <remarks>
/// The rows are written by <c>InAppNotificationJob</c>, not here. <c>userId</c> is always the caller's own (the token's
/// <c>sub</c>): it is never taken from a route or a body.
/// </remarks>
public interface IInAppNotificationService
{
    /// <summary>
    /// A page of the user's notifications, the most recent first (<c>CreatedAt</c>, then <c>Id</c>, descending).
    /// <paramref name="pageSize"/> is held between 1 and <see cref="InAppNotificationLimits.MaxPageSize"/>, <paramref name="page"/>
    /// at 1 or more. <c>TotalCount</c> counts what the filter matches, whatever the page.
    /// </summary>
    Task<InAppNotificationPage> ListAsync(
        string userId,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>How many of the user's notifications are unread: the number of the bell.</summary>
    Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one notification as read. Idempotent: one already read stays as it is and the call succeeds. Raises
    /// <see cref="NotFoundException"/> when the user has no such notification (another user's, another org's, deleted by the
    /// retention, never existed).
    /// </summary>
    Task MarkReadAsync(string userId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks every unread notification of the user as read and returns how many it was. Idempotent.</summary>
    Task<int> MarkAllReadAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the notifications older than <see cref="InAppNotificationLimits.RetentionDays"/> days, of every user and org,
    /// read or not, and returns how many it deleted. For the recurring job; not tenant-filtered on purpose.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>A notification as the client reads it: no text, no person (see <see cref="InAppNotification"/>).</summary>
/// <param name="Id">The notification.</param>
/// <param name="Type">A value of <c>PushTypes</c>; the client writes the sentence from it.</param>
/// <param name="EntityId">The service request (<c>service-request-*</c> types) or the booking the event is about, or <c>null</c>.</param>
/// <param name="CreatedAt">UTC instant of the event.</param>
/// <param name="ReadAt">UTC instant the user read it; <c>null</c> while unread.</param>
public sealed record InAppNotificationItem(Guid Id, string Type, Guid? EntityId, DateTime CreatedAt, DateTime? ReadAt);

/// <summary>One page of <see cref="IInAppNotificationService.ListAsync"/>.</summary>
public sealed record InAppNotificationPage(IReadOnlyList<InAppNotificationItem> Items, int TotalCount, int Page, int PageSize);
