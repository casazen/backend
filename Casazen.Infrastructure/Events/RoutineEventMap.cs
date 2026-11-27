using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// Which routines a domain event starts (NF-21, ADR-022): the <see cref="DomainEventType"/> of the outbox to the
/// <see cref="RoutineEventType"/> of the routine engine. A time trigger is resolved from the kind of its payload. Every other type
/// (<c>ServiceRequestCreated</c>, the Octorate events, the types later epics add) starts no routine.
/// </summary>
public static class RoutineEventMap
{
    private static readonly IReadOnlyDictionary<DomainEventType, RoutineEventType> DomainEvents =
        new Dictionary<DomainEventType, RoutineEventType>
        {
            [DomainEventType.BookingCreated] = RoutineEventType.BookingNew,
            [DomainEventType.BookingConfirmed] = RoutineEventType.BookingConfirmed,
            [DomainEventType.BookingModified] = RoutineEventType.BookingModified,
            [DomainEventType.BookingCancelled] = RoutineEventType.BookingCanceled,
            [DomainEventType.CheckInStarted] = RoutineEventType.CheckinOccurred,
            [DomainEventType.CheckInCompleted] = RoutineEventType.OnlineCheckinCompleted,
            [DomainEventType.CheckOutCompleted] = RoutineEventType.CheckoutOccurred,
            [DomainEventType.IdVerificationCompleted] = RoutineEventType.IdVerificationCompleted,
            [DomainEventType.AlloggiatiReceiptArrived] = RoutineEventType.AlloggiatiReceiptArrived,
            [DomainEventType.ServiceRequestCompleted] = RoutineEventType.ActivityCompleted,
            [DomainEventType.PaymentSucceeded] = RoutineEventType.PaymentSucceeded,
            [DomainEventType.PaymentFailed] = RoutineEventType.PaymentFailed,
            [DomainEventType.GuestMessageReceived] = RoutineEventType.GuestMessageReceived,
            [DomainEventType.ReviewReceived] = RoutineEventType.ReviewReceived,
        };

    private static readonly IReadOnlyDictionary<string, RoutineEventType> TimeTriggerKinds =
        new Dictionary<string, RoutineEventType>(StringComparer.Ordinal)
        {
            [TimeTrigger.BeforeCheckIn] = RoutineEventType.BeforeCheckin,
            [TimeTrigger.AfterCheckIn] = RoutineEventType.AfterCheckin,
            [TimeTrigger.BeforeCheckOut] = RoutineEventType.BeforeCheckout,
            [TimeTrigger.AfterCheckOut] = RoutineEventType.AfterCheckout,
            [TimeTrigger.Weekly] = RoutineEventType.WeeklyRecurrence,
        };

    /// <summary>The routine event type of a domain event that is not a time trigger; <c>null</c> when it starts no routine.</summary>
    public static RoutineEventType? Of(DomainEventType type) =>
        DomainEvents.TryGetValue(type, out var routineEventType) ? routineEventType : null;

    /// <summary>The routine event type of the kind of a time trigger (<see cref="TimeTrigger"/>); <c>null</c> for an unknown kind.</summary>
    public static RoutineEventType? OfTimeTriggerKind(string? kind) =>
        kind is not null && TimeTriggerKinds.TryGetValue(kind, out var routineEventType) ? routineEventType : null;
}
