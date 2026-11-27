using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The push and the e-mail of an alert about a scheduled message that waited for the end of the quiet hours of one person (E05/F07/US01, NF-71, NF-65): the arguments of the Hangfire job
/// scheduled for <see cref="DueAtUtc"/>. Ids, a kind, an instant and the channels that wait: <b>no word of the message, no name, no address</b> (the person is a user id, the address is read
/// again when the job runs).
/// </summary>
public sealed record QueuedScheduledMessageAlert
{
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public Guid BookingId { get; set; }

    /// <summary>The scheduled message.</summary>
    public Guid MessageId { get; set; }

    /// <summary>A code of <see cref="ScheduledMessageAlertKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>When the alert was raised: the same instant the first delivery judged the quiet hours at, and part of the keys of the push and of the bell.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The person who is told (<c>User.Id</c>).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The channels held back: <see cref="NotificationChannels.MobilePush"/> and/or <see cref="NotificationChannels.Email"/>.</summary>
    public NotificationChannels Channels { get; set; }

    /// <summary>When they are due: the end of the quiet hours, and the instant the slot of the e-mail was claimed for.</summary>
    public DateTime DueAtUtc { get; set; }

    public ScheduledMessageAlert ToAlert() => new(OrgId, PropertyId, BookingId, MessageId, Kind, null, OccurredAtUtc);
}

/// <summary>
/// The Hangfire jobs of the alerts about the scheduled messages (E05/F07/US01): what the quiet hours held back (<see cref="DeliverAsync"/>) and the whole alert that failed for somebody
/// (<see cref="RaiseAsync"/>). Both are retried by Hangfire, and neither does anything twice: the keys of the push and of the bell and the slot of the e-mail make a repeated run find the work done.
/// </summary>
public sealed class ScheduledMessageAlertJob(ScheduledMessageAlertDelivery delivery)
{
    public const int MaxAttempts = 5;

    /// <summary>Delivers the channels that waited for the end of the quiet hours of one person; it asks the preferences again before it sends.</summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task DeliverAsync(QueuedScheduledMessageAlert queued, CancellationToken cancellationToken) =>
        delivery.DeliverDeferredAsync(queued, cancellationToken);

    /// <summary>Raises again the whole alert of a message (every person): ids, a kind and an instant only.</summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task RaiseAsync(ScheduledMessageAlert alert, CancellationToken cancellationToken) =>
        delivery.AlertAsync(alert, cancellationToken);
}
