namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kinds of domain event written to the transactional outbox (<see cref="OutboxEvent"/>, ADR-022-R2) and published on the
/// event bus. The column stores the number, so every value has an explicit one: a later task appends a value with the next
/// number, an existing number never changes and is never reused.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="RoutineEventType"/>, the older enum of the routine engine: the routine handler maps these
/// events onto it (E01/F01/US01/T05).
/// </remarks>
public enum DomainEventType
{
    BookingCreated = 1,
    BookingConfirmed = 2,
    BookingModified = 3,
    BookingCancelled = 4,
    CheckInStarted = 5,
    CheckInCompleted = 6,
    CheckOutCompleted = 7,
    IdVerificationCompleted = 8,
    AlloggiatiReceiptArrived = 9,
    ServiceRequestCreated = 10,
    ServiceRequestCompleted = 11,
    PaymentSucceeded = 12,
    PaymentFailed = 13,
    GuestMessageReceived = 14,
    ReviewReceived = 15,

    /// <summary>A scheduled trigger of a stay ("2 days before check-in"), written by <c>TimeEventEmitterJob</c> (ADR-022-R8).</summary>
    TimeTrigger = 16,

    OctorateWebhookReceived = 17,
    OctorateBookingStateChanged = 18,

    /// <summary>
    /// The host declared the property ready after the check-out of a stay, at the check-out wizard or later (E08/F06/US01). About the
    /// booking (<c>EntityId</c>); it closes the cleaning "To do" of that stay. Starts no routine.
    /// </summary>
    PropertyReadyDeclared = 19,

    /// <summary>
    /// The host asked again for the push of a booking to the channel manager (E08/F01/US01/T03,
    /// <c>POST /api/bookings/{id}/octorate-sync/retry</c>): about the booking (<c>EntityId</c>), with the payload of a booking event (origin
    /// System). Runs the push of the booking again and nothing else: no routine and no other consumer of <c>BookingConfirmed</c> (a welcome
    /// message, an alert) runs a second time.
    /// </summary>
    OctorateSyncRequested = 20,
}
