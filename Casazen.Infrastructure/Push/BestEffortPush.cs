using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Queues a push the way every notifier does: after the change is committed, never failing the caller. The payload is built
/// inside the guard (a missing resource or a null name is logged, not thrown), and a push that cannot be queued is logged with the
/// type and the delivery key only (ids, no text). The in-app notification and the real-time update follow from the same call
/// (<see cref="InAppNotificationPushDecorator"/>).
/// </summary>
internal static class BestEffortPush
{
    public static void Enqueue(
        IPushNotificationService? pushNotifications,
        ILogger logger,
        string deliveryKey,
        PushAudience audience,
        Func<PushNotificationPayload> buildPayload)
    {
        if (pushNotifications is null)
            return;

        try
        {
            var payload = buildPayload();
            if (!pushNotifications.Enqueue(deliveryKey, audience, payload))
                logger.LogWarning("Push {Type} (key {DeliveryKey}) was not queued", payload.Type, deliveryKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push (key {DeliveryKey}) could not be prepared", deliveryKey);
        }
    }
}
