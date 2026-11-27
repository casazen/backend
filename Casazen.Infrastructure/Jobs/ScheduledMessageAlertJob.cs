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

    public Guid MessageId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public string UserId { get; set; } = string.Empty;

    public NotificationChannels Channels { get; set; }

    public DateTime DueAtUtc { get; set; }

    public ScheduledMessageAlert ToAlert() => new(OrgId, PropertyId, BookingId, MessageId, Kind, null, OccurredAtUtc);
}

public sealed class ScheduledMessageAlertJob(ScheduledMessageAlertDelivery delivery)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task DeliverAsync(QueuedScheduledMessageAlert queued, CancellationToken cancellationToken) =>
        delivery.DeliverDeferredAsync(queued, cancellationToken);

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task RaiseAsync(ScheduledMessageAlert alert, CancellationToken cancellationToken) =>
        delivery.AlertAsync(alert, cancellationToken);
}
