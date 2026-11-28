using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The push and the email of an approval alert that waited for the end of the quiet hours of one person (E05/F04/US01/T02, NF-68, NF-65): the arguments of the Hangfire
/// job scheduled for <see cref="DueAtUtc"/>. Ids, an instant and the channels that wait: <b>no text, no name, no address</b> (the person is a user id, the address is
/// read again when the job runs).
/// </summary>
public sealed record QueuedApprovalAlert
{
    public Guid OrgId { get; set; }

    public Guid ItemId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string UserId { get; set; } = string.Empty;

    public NotificationChannels Channels { get; set; }

    public DateTime DueAtUtc { get; set; }

    public ApprovalAlert ToAlert() => new(OrgId, ItemId, OccurredAtUtc);
}

/// <summary>
/// The Hangfire jobs of the approval alerts (E05/F04/US01/T02): what the quiet hours held back (<see cref="DeliverAsync"/>) and the whole alert that failed for somebody
/// or whose "To do" could not be raised (<see cref="RaiseAsync"/>). Both are retried by Hangfire, and neither does anything twice: the keys of the push and of the bell, the
/// slot of the e-mail and the open "To do" of the item make a repeated run find the work done.
/// </summary>
public sealed class ApprovalAlertJob(ApprovalAlertDelivery delivery)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task DeliverAsync(QueuedApprovalAlert queued, CancellationToken cancellationToken) =>
        delivery.DeliverDeferredAsync(queued, cancellationToken);

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task RaiseAsync(ApprovalAlert alert, CancellationToken cancellationToken) =>
        delivery.RaiseAsync(alert, cancellationToken);
}
