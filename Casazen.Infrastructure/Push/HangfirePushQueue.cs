using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Arguments of a queued push (Hangfire job argument): who receives it and what it shows. Only the text of the
/// notification (property name, dates, category: never a guest name) and ids; the devices and their tokens are resolved
/// by <see cref="PushDeliveryJob"/> when it runs.
/// </summary>
public sealed class QueuedPush
{
    public PushAudienceKind AudienceKind { get; set; }

    public Guid AudienceId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public Guid? BookingId { get; set; }

    public Guid? ServiceRequestId { get; set; }

    public PushAudience ToAudience() => new(AudienceKind, AudienceId);

    public static QueuedPush From(PushAudience audience, PushNotificationPayload payload) => new()
    {
        AudienceKind = audience.Kind,
        AudienceId = audience.Id,
        Title = payload.Title,
        Body = payload.Body,
        Type = payload.Type,
        Route = payload.Route,
        BookingId = payload.BookingId,
        ServiceRequestId = payload.ServiceRequestId,
    };
}

/// <summary>
/// <see cref="IPushNotificationService"/> on Hangfire (MO-04, A6-29): every push becomes a <see cref="PushDeliveryJob"/>,
/// so the request that caused it never waits for Expo.
/// </summary>
public sealed class HangfirePushQueue(
    IBackgroundJobClient backgroundJobClient,
    ILogger<HangfirePushQueue> logger) : IPushNotificationService
{
    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKey);
            ArgumentNullException.ThrowIfNull(audience);
            ArgumentNullException.ThrowIfNull(payload);
            if (deliveryKey.Length > 200)
                throw new ArgumentException("A delivery key has at most 200 characters.", nameof(deliveryKey));

            // MO-03 (A6-19): a tap must open a screen that exists in the app.
            if (!PushRoutes.IsAppRoute(payload.Route))
                throw new ArgumentException($"Push route '{payload.Route}' is not a screen of the app.", nameof(payload));

            var push = QueuedPush.From(audience, payload);
            var jobId = backgroundJobClient.Enqueue<PushDeliveryJob>(job =>
                job.SendAsync(deliveryKey, push, CancellationToken.None));
            logger.LogInformation(
                "Push {Type} queued for {Audience} {AudienceId} (key {DeliveryKey}, job {JobId})",
                payload.Type,
                audience.Kind,
                audience.Id,
                deliveryKey,
                jobId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Push {Type} could not be queued (key {DeliveryKey})", payload?.Type, deliveryKey);
            return false;
        }
    }
}
