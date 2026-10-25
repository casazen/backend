using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Event dispatcher for routine automation (NF-26, ADR-022).
/// Maps domain events to routine triggers.
/// In a full implementation, this would integrate with an event bus/outbox pattern.
/// </summary>
public class RoutineEventDispatcher(
    IRoutineService routineService,
    ILogger<RoutineEventDispatcher> logger)
{
    /// <summary>
    /// Dispatch a booking event to routines.
    /// </summary>
    public async Task DispatchBookingEventAsync(
        string eventType,
        Guid bookingId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var routineEventType = MapBookingEvent(eventType);
        if (routineEventType == null)
        {
            logger.LogDebug("Booking event {EventType} does not map to a routine event", eventType);
            return;
        }

        logger.LogInformation(
            "Dispatching booking event {EventType} for booking {BookingId} to routines",
            eventType, bookingId);

        await routineService.TriggerAsync(
            routineEventType.Value,
            $"booking-{bookingId}",
            payload,
            cancellationToken);
    }

    /// <summary>
    /// Dispatch a payment event to routines.
    /// </summary>
    public async Task DispatchPaymentEventAsync(
        string eventType,
        Guid paymentId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var routineEventType = MapPaymentEvent(eventType);
        if (routineEventType == null)
        {
            logger.LogDebug("Payment event {EventType} does not map to a routine event", eventType);
            return;
        }

        logger.LogInformation(
            "Dispatching payment event {EventType} for payment {PaymentId} to routines",
            eventType, paymentId);

        await routineService.TriggerAsync(
            routineEventType.Value,
            $"payment-{paymentId}",
            payload,
            cancellationToken);
    }

    /// <summary>
    /// Dispatch a guest check-in event to routines.
    /// </summary>
    public async Task DispatchCheckinEventAsync(
        string eventType,
        Guid bookingId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        RoutineEventType routineEventType;

        switch (eventType.ToLowerInvariant())
        {
            case "completed":
                routineEventType = RoutineEventType.OnlineCheckinCompleted;
                break;
            case "id_verified":
                routineEventType = RoutineEventType.IdVerificationCompleted;
                break;
            default:
                logger.LogDebug("Check-in event {EventType} does not map to a routine event", eventType);
                return;
        }

        logger.LogInformation(
            "Dispatching check-in event {EventType} for booking {BookingId} to routines",
            eventType, bookingId);

        await routineService.TriggerAsync(
            routineEventType,
            $"checkin-{bookingId}-{eventType}",
            payload,
            cancellationToken);
    }

    /// <summary>
    /// Dispatch an Alloggiati receipt event to routines.
    /// </summary>
    public async Task DispatchAlloggiatiEventAsync(
        Guid bookingId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Dispatching Alloggiati receipt event for booking {BookingId} to routines",
            bookingId);

        await routineService.TriggerAsync(
            RoutineEventType.AlloggiatiReceiptArrived,
            $"alloggiati-{bookingId}",
            payload,
            cancellationToken);
    }

    /// <summary>
    /// Dispatch an activity completion event to routines.
    /// </summary>
    public async Task DispatchActivityEventAsync(
        Guid activityId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Dispatching activity completed event for activity {ActivityId} to routines",
            activityId);

        await routineService.TriggerAsync(
            RoutineEventType.ActivityCompleted,
            $"activity-{activityId}",
            payload,
            cancellationToken);
    }

    private RoutineEventType? MapBookingEvent(string eventType)
    {
        return eventType.ToLowerInvariant() switch
        {
            "created" or "new" => RoutineEventType.BookingNew,
            "confirmed" => RoutineEventType.BookingConfirmed,
            "modified" => RoutineEventType.BookingModified,
            "canceled" or "cancelled" => RoutineEventType.BookingCanceled,
            "checkin_occurred" => RoutineEventType.CheckinOccurred,
            "checkout_occurred" => RoutineEventType.CheckoutOccurred,
            _ => null
        };
    }

    private RoutineEventType? MapPaymentEvent(string eventType)
    {
        return eventType.ToLowerInvariant() switch
        {
            "succeeded" or "success" => RoutineEventType.PaymentSucceeded,
            "failed" => RoutineEventType.PaymentFailed,
            _ => null
        };
    }
}
