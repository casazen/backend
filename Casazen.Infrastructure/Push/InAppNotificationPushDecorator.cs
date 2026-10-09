using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Gives every push an in-app notification (UI-12a): it forwards the push to the real queue (<see cref="HangfirePushQueue"/>) and,
/// with <c>Features:InAppNotifications</c> on, queues an <see cref="InAppNotificationJob"/> for the same event, with the same
/// delivery key and audience, so the bell of the shell lists what the phone was told. The callers of
/// <see cref="IPushNotificationService"/> (the booking, service request and showcase notifiers, the stay alerts) do not change and
/// do not know it exists. Runbook: <c>docs/runbooks/in-app-notifications.md</c>.
/// </summary>
/// <remarks>
/// <para><b>The push is never touched.</b> The inner queue is called first, with the same arguments, and its answer is returned
/// whatever happens to the notification: a notification that cannot be queued is logged and costs the push nothing. With the flag
/// off this class is a pass-through: no job, no read, no log.</para>
/// <para><b>Why a job, and why users there.</b> The audience of a push is a booking, a property or a supplier org, not a list of
/// people: the push job turns it into devices when it runs, and the notification job turns it into users the same way, so
/// nothing is resolved inside the request that caused the event. A push whose audience has nobody (no user, no device) writes
/// nothing and is not an error. The notification does not depend on the push being queued: a push refused for its route, or a
/// Hangfire that took the first job and failed the second, leave the other one alone.</para>
/// <para>Only the type, the audience, the delivery key and the id of the booking or service request cross into the job: the push
/// title and body (property, dates, category) are not copied.</para>
/// </remarks>
public sealed class InAppNotificationPushDecorator(
    IPushNotificationService inner,
    IBackgroundJobClient backgroundJobClient,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<InAppNotificationPushDecorator> logger) : IPushNotificationService
{
    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        var pushQueued = inner.Enqueue(deliveryKey, audience, payload);

        if (featureFlags.IsEnabled(FeatureFlags.InAppNotifications))
            QueueNotification(deliveryKey, audience, payload);

        return pushQueued;
    }

    private void QueueNotification(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deliveryKey)
                || deliveryKey.Length > InAppNotification.DeliveryKeyMaxLength
                || audience is null
                || payload is null
                || string.IsNullOrWhiteSpace(payload.Type)
                || payload.Type.Length > InAppNotification.TypeMaxLength)
            {
                logger.LogWarning(
                    "In-app notification {Type} not queued (key {DeliveryKey}): the key or the type is empty or too long",
                    payload?.Type,
                    deliveryKey);
                return;
            }

            var notification = QueuedInAppNotification.From(audience, payload, timeProvider.GetUtcNow().UtcDateTime);
            var jobId = backgroundJobClient.Enqueue<InAppNotificationJob>(job =>
                job.CreateAsync(deliveryKey, notification, CancellationToken.None));
            logger.LogInformation(
                "In-app notification {Type} queued for {Audience} {AudienceId} (key {DeliveryKey}, job {JobId})",
                payload.Type,
                audience.Kind,
                audience.Id,
                deliveryKey,
                jobId);
        }
        catch (Exception ex)
        {
            // The push is already queued (or has failed on its own): the notification never fails the caller.
            logger.LogError(ex, "In-app notification {Type} could not be queued (key {DeliveryKey})", payload?.Type, deliveryKey);
        }
    }
}
