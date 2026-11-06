using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The push and the email of a message alert that waited for the end of the quiet hours of one person (E05/F03/US01/T03, NF-65): the arguments of
/// the Hangfire job scheduled for <see cref="DueAtUtc"/>. Ids, an instant and the channels that wait: <b>no text, no name, no address</b> (the person
/// is a user id, the address is read again when the job runs).
/// </summary>
public sealed record QueuedMessageAlert
{
    public Guid OrgId { get; set; }

    public HostMessageSource Source { get; set; }

    public NotificationEventType EventType { get; set; }

    public Guid ThreadId { get; set; }

    public Guid MessageId { get; set; }

    /// <summary>When the message was written: the same instant the first delivery judged the quiet hours at.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The person who is told (<c>User.Id</c>).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The channels held back: <see cref="NotificationChannels.MobilePush"/> and/or <see cref="NotificationChannels.Email"/>.</summary>
    public NotificationChannels Channels { get; set; }

    /// <summary>When they are due: the end of the quiet hours, and the instant the slot of the email was claimed for.</summary>
    public DateTime DueAtUtc { get; set; }

    public HostMessageAlert ToAlert() => new(OrgId, Source, EventType, ThreadId, MessageId, OccurredAtUtc);
}

/// <summary>
/// Hangfire job that delivers what a message alert held back for the quiet hours (<see cref="HostMessageAlertDelivery.DeliverDeferredAsync"/>):
/// the delivery asks the preferences again before it sends, so a person who lost the property, changed its choice or was deactivated since is not
/// told. Retried by Hangfire: the push has its delivery key (a device gets it once), and the slot of the email was claimed when the alert was scheduled, so the job only queues it.
/// </summary>
public sealed class HostMessageAlertDueJob(HostMessageAlertDelivery delivery)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task DeliverAsync(QueuedMessageAlert queued, CancellationToken cancellationToken) =>
        delivery.DeliverDeferredAsync(queued, cancellationToken);
}
